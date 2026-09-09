using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.GiftCards;

/// <summary>Hediye çeki görünüm modeli (cari adı Contacts join ile doldurulur).</summary>
public record GiftCardDto(
    Guid Id, string Code, decimal InitialBalance, decimal Balance, string Status,
    Guid? ContactId, string? ContactName, DateTime? ExpiresAt, string? Note, DateTime CreatedAt);

/// <summary>Yeni hediye çeki kesim (issue) isteği.</summary>
public record IssueGiftCardRequest(decimal InitialBalance, Guid? ContactId, DateTime? ExpiresAt, string? Note);

/// <summary>Manuel bakiye harcama (redeem) isteği.</summary>
public record RedeemGiftCardRequest(decimal Amount);

public interface IGiftCardService
{
    /// <summary>Tüm hediye çeklerini (en yeni önce) listeler.</summary>
    Task<IReadOnlyList<GiftCardDto>> ListAsync(CancellationToken ct = default);

    /// <summary>Koda göre bakiye/durum sorgular; yoksa <see cref="NotFoundException"/>.</summary>
    Task<GiftCardDto> GetByCodeAsync(string code, CancellationToken ct = default);

    /// <summary>Yeni çek keser: benzersiz kod üretir, bakiye = başlangıç, durum = Active.</summary>
    Task<GiftCardDto> IssueAsync(IssueGiftCardRequest req, CancellationToken ct = default);

    /// <summary>Koddan tutar düşer; bakiye 0 olursa durum "Used" olur.</summary>
    Task<GiftCardDto> RedeemAsync(string code, decimal amount, CancellationToken ct = default);

    /// <summary>Çeki iptal eder (durum "Cancelled").</summary>
    Task<GiftCardDto> CancelAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Hediye çeki servisi — kesim, bakiye sorgu ve manuel harcama.
/// Kod Guid'den türetilen benzersiz 12 haneli sayısal değerdir; DB'de çakışma olursa yeniden üretilir.
/// Şube izolasyonu yoktur (GiftCard entity'sinde BranchId bulunmaz).
/// </summary>
public sealed class GiftCardService : IGiftCardService
{
    private readonly IApplicationDbContext _db;

    public GiftCardService(IApplicationDbContext db) => _db = db;

    public async Task<IReadOnlyList<GiftCardDto>> ListAsync(CancellationToken ct = default)
    {
        return await (from g in _db.GiftCards.AsNoTracking()
                      join c in _db.Contacts on g.ContactId equals (Guid?)c.Id into gc
                      from c in gc.DefaultIfEmpty()
                      orderby g.CreatedAt descending
                      select new GiftCardDto(
                          g.Id, g.Code, g.InitialBalance, g.Balance, g.Status,
                          g.ContactId, c != null ? c.Name : null, g.ExpiresAt, g.Note, g.CreatedAt))
                     .ToListAsync(ct);
    }

    public async Task<GiftCardDto> GetByCodeAsync(string code, CancellationToken ct = default)
    {
        code = (code ?? string.Empty).Trim();
        var card = await _db.GiftCards.AsNoTracking().FirstOrDefaultAsync(g => g.Code == code, ct)
            ?? throw NotFoundException.For("Hediye çeki", code);
        return await MapAsync(card, ct);
    }

    public async Task<GiftCardDto> IssueAsync(IssueGiftCardRequest req, CancellationToken ct = default)
    {
        if (req.InitialBalance <= 0m) throw new BusinessRuleException("Başlangıç bakiyesi sıfırdan büyük olmalı.");

        if (req.ContactId is Guid cid)
        {
            _ = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == cid, ct)
                ?? throw NotFoundException.For("Cari", cid);
        }

        var code = await GenerateUniqueCodeAsync(ct);
        var card = new GiftCard
        {
            Code = code,
            InitialBalance = req.InitialBalance,
            Balance = req.InitialBalance,
            Status = "Active",
            ContactId = req.ContactId,
            ExpiresAt = req.ExpiresAt,
            Note = req.Note?.Trim(),
        };
        _db.GiftCards.Add(card);
        await _db.SaveChangesAsync(ct);
        return await MapAsync(card, ct);
    }

    public async Task<GiftCardDto> RedeemAsync(string code, decimal amount, CancellationToken ct = default)
    {
        if (amount <= 0m) throw new BusinessRuleException("Harcama tutarı sıfırdan büyük olmalı.");

        code = (code ?? string.Empty).Trim();
        var card = await _db.GiftCards.FirstOrDefaultAsync(g => g.Code == code, ct)
            ?? throw NotFoundException.For("Hediye çeki", code);

        if (card.Status != "Active") throw new BusinessRuleException("Hediye çeki aktif değil.");
        if (card.ExpiresAt is DateTime exp && exp < DateTime.UtcNow)
            throw new BusinessRuleException("Hediye çekinin süresi dolmuş.");
        if (amount > card.Balance) throw new BusinessRuleException("Harcama tutarı bakiyeden büyük olamaz.");

        card.Balance -= amount;
        if (card.Balance == 0m) card.Status = "Used";
        await _db.SaveChangesAsync(ct);
        return await MapAsync(card, ct);
    }

    public async Task<GiftCardDto> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var card = await _db.GiftCards.FirstOrDefaultAsync(g => g.Id == id, ct)
            ?? throw NotFoundException.For("Hediye çeki", id);
        card.Status = "Cancelled";
        await _db.SaveChangesAsync(ct);
        return await MapAsync(card, ct);
    }

    /// <summary>Guid'den 12 haneli sayısal kod üretir; DB'de çakışma varsa benzersiz olana dek yineler.</summary>
    private async Task<string> GenerateUniqueCodeAsync(CancellationToken ct)
    {
        for (var i = 0; i < 20; i++)
        {
            var num = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0) % 1_000_000_000_000UL;
            var code = num.ToString("D12");
            if (!await _db.GiftCards.AnyAsync(g => g.Code == code, ct)) return code;
        }
        throw new BusinessRuleException("Benzersiz hediye çeki kodu üretilemedi.");
    }

    /// <summary>Entity'yi DTO'ya çevirir; cari adı varsa Contacts'tan çeker.</summary>
    private async Task<GiftCardDto> MapAsync(GiftCard g, CancellationToken ct)
    {
        string? contactName = null;
        if (g.ContactId is Guid cid)
            contactName = await _db.Contacts.Where(c => c.Id == cid).Select(c => c.Name).FirstOrDefaultAsync(ct);
        return new GiftCardDto(
            g.Id, g.Code, g.InitialBalance, g.Balance, g.Status,
            g.ContactId, contactName, g.ExpiresAt, g.Note, g.CreatedAt);
    }
}
