using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Marketplace;

/// <summary>Pazaryeri bağlantısı (kanal + kimlik) yönetimi. API kimlikleri şifreli saklanır, asla dönmez.</summary>
public sealed class MarketplaceConnectionService : IMarketplaceConnectionService
{
    private readonly IApplicationDbContext _db;
    private readonly ISecretProtector _protector;
    private readonly IMarketplaceProviderFactory _providers;

    public MarketplaceConnectionService(IApplicationDbContext db, ISecretProtector protector, IMarketplaceProviderFactory providers)
    {
        _db = db;
        _protector = protector;
        _providers = providers;
    }

    public async Task<IReadOnlyList<MarketplaceConnectionDto>> ListAsync(CancellationToken ct = default)
    {
        var list = await _db.MarketplaceConnections.OrderBy(c => c.Channel).ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    public async Task<MarketplaceConnectionDto> CreateAsync(CreateConnectionRequest req, CancellationToken ct = default)
    {
        var channel = req.Channel?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(channel)) throw new BusinessRuleException("Kanal gerekli.");
        if (string.IsNullOrWhiteSpace(req.SupplierId)) throw new BusinessRuleException("Satıcı ID gerekli.");
        if (string.IsNullOrWhiteSpace(req.ApiKey) || string.IsNullOrWhiteSpace(req.ApiSecret))
            throw new BusinessRuleException("API anahtarı ve gizli anahtar gerekli.");
        if (_providers.Get(channel) is null) throw new BusinessRuleException($"Desteklenmeyen pazaryeri: {channel}");
        if (await _db.MarketplaceConnections.AnyAsync(c => c.Channel == channel, ct))
            throw new ConflictException($"{channel} bağlantısı zaten var.");

        var conn = new MarketplaceConnection
        {
            Channel = channel,
            SupplierId = req.SupplierId.Trim(),
            ApiKeyEnc = _protector.Protect(req.ApiKey.Trim()),
            ApiSecretEnc = _protector.Protect(req.ApiSecret.Trim()),
            IsActive = true,
            CommissionRate = Math.Clamp(req.CommissionRate, 0m, 100m),
            ShippingCost = Math.Max(0m, req.ShippingCost),
        };
        _db.MarketplaceConnections.Add(conn);
        await _db.SaveChangesAsync(ct);
        return ToDto(conn);
    }

    public async Task<MarketplaceConnectionDto> UpdateAsync(Guid id, UpdateConnectionRequest req, CancellationToken ct = default)
    {
        var conn = await _db.MarketplaceConnections.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw NotFoundException.For("Bağlantı", id);
        if (!string.IsNullOrWhiteSpace(req.SupplierId)) conn.SupplierId = req.SupplierId.Trim();
        if (!string.IsNullOrWhiteSpace(req.ApiKey)) conn.ApiKeyEnc = _protector.Protect(req.ApiKey.Trim());
        if (!string.IsNullOrWhiteSpace(req.ApiSecret)) conn.ApiSecretEnc = _protector.Protect(req.ApiSecret.Trim());
        conn.IsActive = req.IsActive;
        conn.CommissionRate = Math.Clamp(req.CommissionRate, 0m, 100m);
        conn.ShippingCost = Math.Max(0m, req.ShippingCost);
        await _db.SaveChangesAsync(ct);
        return ToDto(conn);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var conn = await _db.MarketplaceConnections.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw NotFoundException.For("Bağlantı", id);
        _db.MarketplaceConnections.Remove(conn);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<SyncResultDto> TestAsync(Guid id, CancellationToken ct = default)
    {
        var conn = await _db.MarketplaceConnections.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw NotFoundException.For("Bağlantı", id);
        var provider = _providers.Get(conn.Channel) ?? throw new BusinessRuleException("Sağlayıcı bulunamadı.");
        var creds = new MarketplaceCredentials(conn.SupplierId,
            _protector.Unprotect(conn.ApiKeyEnc), _protector.Unprotect(conn.ApiSecretEnc));
        var r = await provider.TestConnectionAsync(creds, ct);
        return new SyncResultDto(r.Success, r.Success ? "Bağlantı çalışıyor." : r.Error ?? "Bağlantı başarısız.", 0, 0, DateTime.UtcNow);
    }

    private static MarketplaceConnectionDto ToDto(MarketplaceConnection c) => new(
        c.Id, c.Channel, c.SupplierId, c.IsActive, c.LastStockSyncAt, c.LastOrderSyncAt,
        c.LastStatus, c.LastMessage, !string.IsNullOrEmpty(c.ApiKeyEnc), c.CreatedAt,
        c.CommissionRate, c.ShippingCost);
}
