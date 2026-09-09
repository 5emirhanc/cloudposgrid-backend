using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Files;

/// <summary>Bir eke ait okuma modeli (dosya URL'i + metadata).</summary>
public record AttachmentDto(
    Guid Id, string OwnerType, Guid OwnerId, string Url, string FileName,
    string? ContentType, string? Note, DateTime CreatedAt);

/// <summary>Yeni ek ekleme isteği. Url, önceden api/uploads ile yüklenip dönen erişim adresidir.</summary>
public record AddAttachmentRequest(
    string OwnerType, Guid OwnerId, string Url, string FileName, string? ContentType, string? Note);

public interface IAttachmentService
{
    /// <summary>Bir sahip kaydına (ownerType + ownerId) iliştirilmiş ekleri en yeniden eskiye listeler.</summary>
    Task<IReadOnlyList<AttachmentDto>> ListAsync(string ownerType, Guid ownerId, CancellationToken ct = default);

    /// <summary>Yüklenmiş bir dosyanın URL metadata'sını ek olarak kaydeder.</summary>
    Task<AttachmentDto> AddAsync(AddAttachmentRequest req, CancellationToken ct = default);

    /// <summary>Ek kaydını (metadata) siler. Fiziksel dosyayı silmez.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Genel dosya/ek metadata yönetimi. Dosya yükleme (api/uploads) ayrı yaşar; bu servis yalnız dönen
/// URL'i sahip kayda (polimorfik OwnerType+OwnerId) bağlar. Şube izolasyonu gerekmez (Attachment'ta BranchId yok).
/// </summary>
public sealed class AttachmentService : IAttachmentService
{
    private readonly IApplicationDbContext _db;

    public AttachmentService(IApplicationDbContext db) => _db = db;

    /// <summary>İzinli sahip tipleri — girdi bunlardan birine (büyük/küçük harf bağımsız) normalize edilir.</summary>
    private static readonly string[] AllowedOwnerTypes = { "Contact", "Invoice", "Product", "Order", "Warranty" };

    public async Task<IReadOnlyList<AttachmentDto>> ListAsync(string ownerType, Guid ownerId, CancellationToken ct = default)
    {
        var type = NormalizeOwnerType(ownerType);
        return await _db.Attachments.AsNoTracking()
            .Where(a => a.OwnerType == type && a.OwnerId == ownerId)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new AttachmentDto(a.Id, a.OwnerType, a.OwnerId, a.Url, a.FileName, a.ContentType, a.Note, a.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<AttachmentDto> AddAsync(AddAttachmentRequest req, CancellationToken ct = default)
    {
        var type = NormalizeOwnerType(req.OwnerType);
        if (req.OwnerId == Guid.Empty) throw new BusinessRuleException("Sahip kaydı (OwnerId) zorunlu.");
        if (string.IsNullOrWhiteSpace(req.Url)) throw new BusinessRuleException("Dosya URL'i zorunlu.");
        if (string.IsNullOrWhiteSpace(req.FileName)) throw new BusinessRuleException("Dosya adı zorunlu.");

        var entity = new Attachment
        {
            OwnerType = type,
            OwnerId = req.OwnerId,
            Url = req.Url.Trim(),
            FileName = req.FileName.Trim(),
            ContentType = req.ContentType?.Trim(),
            Note = req.Note?.Trim(),
        };
        _db.Attachments.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Map(entity);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _db.Attachments.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw NotFoundException.For("Ek", id);
        _db.Attachments.Remove(entity);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Girdiyi izinli sahip tiplerinden birine (kanonik yazımıyla) çevirir; geçersizse iş kuralı hatası atar.</summary>
    private static string NormalizeOwnerType(string ownerType)
    {
        if (string.IsNullOrWhiteSpace(ownerType)) throw new BusinessRuleException("Sahip tipi (ownerType) zorunlu.");
        return AllowedOwnerTypes.FirstOrDefault(t => string.Equals(t, ownerType.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new BusinessRuleException($"Geçersiz sahip tipi: {ownerType}. İzinli: {string.Join(", ", AllowedOwnerTypes)}");
    }

    private static AttachmentDto Map(Attachment a) => new(
        a.Id, a.OwnerType, a.OwnerId, a.Url, a.FileName, a.ContentType, a.Note, a.CreatedAt);
}
