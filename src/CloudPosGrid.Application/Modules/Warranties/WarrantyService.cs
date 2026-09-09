using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Warranties;

/// <summary>Garanti kaydı görünüm modeli. ExpiryDate ve IsExpired kayıttan hesaplanır.</summary>
public record WarrantyRecordDto(
    Guid Id, string CustomerName, Guid? ContactId, string? ContactName,
    string ProductName, string? SerialNo, DateTime PurchaseDate, int WarrantyMonths, string? Note, Guid? BranchId)
{
    /// <summary>Garanti bitiş tarihi = satın alma tarihi + garanti süresi (ay).</summary>
    public DateTime ExpiryDate => PurchaseDate.AddMonths(WarrantyMonths);

    /// <summary>Garanti süresi doldu mu (bitiş tarihi geçmişte kaldıysa true).</summary>
    public bool IsExpired => ExpiryDate < DateTime.UtcNow;
}

/// <summary>Yeni garanti kaydı oluşturma isteği.</summary>
public record CreateWarrantyRecordRequest(
    string CustomerName, string ProductName, DateTime PurchaseDate, int WarrantyMonths,
    string? SerialNo = null, string? Note = null, Guid? ContactId = null);

/// <summary>Garanti kaydı güncelleme isteği.</summary>
public record UpdateWarrantyRecordRequest(
    string CustomerName, string ProductName, DateTime PurchaseDate, int WarrantyMonths,
    string? SerialNo = null, string? Note = null, Guid? ContactId = null);

public interface IWarrantyService
{
    /// <summary>Garanti kayıtlarını listeler. <paramref name="includeExpired"/> false ise süresi dolmuş kayıtlar gizlenir.</summary>
    Task<List<WarrantyRecordDto>> ListAsync(bool includeExpired = false, CancellationToken ct = default);
    Task<WarrantyRecordDto> CreateAsync(CreateWarrantyRecordRequest req, CancellationToken ct = default);
    Task<WarrantyRecordDto> UpdateAsync(Guid id, UpdateWarrantyRecordRequest req, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public sealed class WarrantyService : IWarrantyService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentBranch _branch;

    public WarrantyService(IApplicationDbContext db, ICurrentBranch branch)
    {
        _db = db;
        _branch = branch;
    }

    public async Task<List<WarrantyRecordDto>> ListAsync(bool includeExpired = false, CancellationToken ct = default)
    {
        IQueryable<WarrantyRecord> q = _db.WarrantyRecords;
        // Çok-şube: seçili şube başlığı varsa yalnız o şubenin kayıtları (şubeye kilitli kullanıcıda
        // global sorgu filtresi zaten kısıtlar; başlık, kısıtsız kullanıcının şube seçimidir).
        if (_branch.HeaderBranchId is Guid b) q = q.Where(w => w.BranchId == b);

        // Süre filtresini SQL'e it: varsayılan "aktif" görünümde süresi dolmuş kayıtlar hiç okunmaz
        // (garanti kayıtları hiç temizlenmediğinden tablo zamanla büyür — bellekte süzmek tüm geçmişi
        // her açılışta transfer ederdi). Aktif = satın alma + garanti ayı >= şimdi. Npgsql AddMonths'ı
        // make_interval'a çevirir; süzme sunucuda gerçekleşir.
        if (!includeExpired)
        {
            var now = DateTime.UtcNow;
            q = q.Where(w => w.PurchaseDate.AddMonths(w.WarrantyMonths) >= now);
        }

        return await q.OrderByDescending(w => w.PurchaseDate)
            .Select(w => new WarrantyRecordDto(
                w.Id, w.CustomerName, w.ContactId, w.Contact != null ? w.Contact.Name : null,
                w.ProductName, w.SerialNo, w.PurchaseDate, w.WarrantyMonths, w.Note, w.BranchId))
            .ToListAsync(ct);
    }

    public async Task<WarrantyRecordDto> CreateAsync(CreateWarrantyRecordRequest req, CancellationToken ct = default)
    {
        Validate(req.CustomerName, req.ProductName, req.WarrantyMonths);
        if (req.ContactId is Guid rc && !await _db.Contacts.AnyAsync(c => c.Id == rc, ct))
            throw NotFoundException.For("Cari", rc);

        var rec = new WarrantyRecord
        {
            CustomerName = req.CustomerName.Trim(),
            ContactId = req.ContactId,
            ProductName = req.ProductName.Trim(),
            SerialNo = string.IsNullOrWhiteSpace(req.SerialNo) ? null : req.SerialNo.Trim(),
            PurchaseDate = req.PurchaseDate,
            WarrantyMonths = req.WarrantyMonths,
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
            BranchId = _branch.HeaderBranchId,
        };
        _db.WarrantyRecords.Add(rec);
        await _db.SaveChangesAsync(ct);

        // Navigation Add sonrası dolmaz; DTO'da cari adını gösterebilmek için elle bağla.
        if (rec.ContactId is Guid cid)
            rec.Contact = await _db.Contacts.FirstOrDefaultAsync(x => x.Id == cid, ct);
        return Map(rec);
    }

    public async Task<WarrantyRecordDto> UpdateAsync(Guid id, UpdateWarrantyRecordRequest req, CancellationToken ct = default)
    {
        var rec = await _db.WarrantyRecords.FirstOrDefaultAsync(w => w.Id == id, ct)
            ?? throw NotFoundException.For("Garanti kaydı", id);
        Validate(req.CustomerName, req.ProductName, req.WarrantyMonths);
        if (req.ContactId is Guid rc && !await _db.Contacts.AnyAsync(c => c.Id == rc, ct))
            throw NotFoundException.For("Cari", rc);

        rec.CustomerName = req.CustomerName.Trim();
        rec.ContactId = req.ContactId;
        rec.ProductName = req.ProductName.Trim();
        rec.SerialNo = string.IsNullOrWhiteSpace(req.SerialNo) ? null : req.SerialNo.Trim();
        rec.PurchaseDate = req.PurchaseDate;
        rec.WarrantyMonths = req.WarrantyMonths;
        rec.Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        await _db.SaveChangesAsync(ct);

        if (rec.ContactId is Guid cid)
            rec.Contact = await _db.Contacts.FirstOrDefaultAsync(x => x.Id == cid, ct);
        return Map(rec);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var rec = await _db.WarrantyRecords.FirstOrDefaultAsync(w => w.Id == id, ct)
            ?? throw NotFoundException.For("Garanti kaydı", id);
        _db.WarrantyRecords.Remove(rec);
        await _db.SaveChangesAsync(ct);
    }

    private static void Validate(string customerName, string productName, int warrantyMonths)
    {
        if (string.IsNullOrWhiteSpace(customerName))
            throw new BusinessRuleException("Müşteri adı zorunlu.");
        if (string.IsNullOrWhiteSpace(productName))
            throw new BusinessRuleException("Ürün adı zorunlu.");
        if (warrantyMonths <= 0)
            throw new BusinessRuleException("Garanti süresi (ay) sıfırdan büyük olmalı.");
    }

    private static WarrantyRecordDto Map(WarrantyRecord w) => new(
        w.Id, w.CustomerName, w.ContactId, w.Contact?.Name,
        w.ProductName, w.SerialNo, w.PurchaseDate, w.WarrantyMonths, w.Note, w.BranchId);
}
