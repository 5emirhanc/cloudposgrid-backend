using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Marketplace;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Pazaryeri (Trendyol) entegrasyonu: bağlantı + ürün eşleştirme yönetimi (Owner/Admin) ve çekilen
/// sipariş listesi. Tümü Kurumsal pakete özeldir (EnsureMarketplaceAsync). Bildirim çanı için
/// hafif "orders/pending" ucu tüm rollere açıktır (yetki yoksa boş döner).
/// </summary>
[ApiController]
[Route("api/marketplace")]
[Authorize]
public class MarketplaceController : ControllerBase
{
    private readonly IMarketplaceConnectionService _connections;
    private readonly IMarketplaceListingService _listings;
    private readonly IMarketplaceOrderService _orders;
    private readonly IMarketplaceSyncService _sync;
    private readonly IPlanEntitlementProvider _entitlements;

    public MarketplaceController(
        IMarketplaceConnectionService connections, IMarketplaceListingService listings,
        IMarketplaceOrderService orders, IMarketplaceSyncService sync, IPlanEntitlementProvider entitlements)
    {
        _connections = connections;
        _listings = listings;
        _orders = orders;
        _sync = sync;
        _entitlements = entitlements;
    }

    private async Task EnsureMarketplaceAsync(CancellationToken ct)
    {
        var e = await _entitlements.GetAsync(ct);
        if (!e.MarketplaceIntegration)
            throw new PlanUpgradeException("Pazaryeri entegrasyonu Kurumsal pakete özeldir. Kullanmak için paketinizi yükseltin.");
    }

    // ---- Bağlantılar ----
    [HttpGet("connections")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<IReadOnlyList<MarketplaceConnectionDto>>> GetConnections(CancellationToken ct)
    {
        await EnsureMarketplaceAsync(ct);
        return Ok(await _connections.ListAsync(ct));
    }

    [HttpPost("connections")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<MarketplaceConnectionDto>> CreateConnection(CreateConnectionRequest req, CancellationToken ct)
    {
        await EnsureMarketplaceAsync(ct);
        return Ok(await _connections.CreateAsync(req, ct));
    }

    [HttpPut("connections/{id:guid}")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<MarketplaceConnectionDto>> UpdateConnection(Guid id, UpdateConnectionRequest req, CancellationToken ct)
    {
        await EnsureMarketplaceAsync(ct);
        return Ok(await _connections.UpdateAsync(id, req, ct));
    }

    [HttpDelete("connections/{id:guid}")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<IActionResult> DeleteConnection(Guid id, CancellationToken ct)
    {
        await EnsureMarketplaceAsync(ct);
        await _connections.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("connections/{id:guid}/test")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<SyncResultDto>> TestConnection(Guid id, CancellationToken ct)
    {
        await EnsureMarketplaceAsync(ct);
        return Ok(await _connections.TestAsync(id, ct));
    }

    [HttpPost("connections/{id:guid}/sync")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<SyncResultDto>> SyncNow(Guid id, CancellationToken ct)
    {
        await EnsureMarketplaceAsync(ct);
        return Ok(await _sync.SyncConnectionAsync(id, ct));
    }

    // ---- Eşleştirme (ürün ↔ pazaryeri barkodu) ----
    [HttpGet("listings")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<IReadOnlyList<MarketplaceListingDto>>> GetListings(CancellationToken ct)
    {
        await EnsureMarketplaceAsync(ct);
        return Ok(await _listings.ListAsync(ct));
    }

    [HttpPost("listings")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<MarketplaceListingDto>> CreateListing(CreateListingRequest req, CancellationToken ct)
    {
        await EnsureMarketplaceAsync(ct);
        return Ok(await _listings.CreateAsync(req, ct));
    }

    [HttpPut("listings/{id:guid}")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<MarketplaceListingDto>> UpdateListing(Guid id, UpdateListingRequest req, CancellationToken ct)
    {
        await EnsureMarketplaceAsync(ct);
        return Ok(await _listings.UpdateAsync(id, req, ct));
    }

    [HttpDelete("listings/{id:guid}")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<IActionResult> DeleteListing(Guid id, CancellationToken ct)
    {
        await EnsureMarketplaceAsync(ct);
        await _listings.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("listings/auto-match")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<object>> AutoMatch(CancellationToken ct)
    {
        await EnsureMarketplaceAsync(ct);
        var count = await _listings.AutoMatchAsync(ct);
        return Ok(new { matched = count });
    }

    // ---- İlan açma (createProducts) referans verileri + gönderim ----
    [HttpGet("trendyol/categories")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<IReadOnlyList<MarketplaceCategory>>> GetCategories(CancellationToken ct)
    {
        await EnsureMarketplaceAsync(ct);
        return Ok(await _listings.GetCategoriesAsync(ct));
    }

    [HttpGet("trendyol/categories/{categoryId:int}/attributes")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<IReadOnlyList<MarketplaceCategoryAttribute>>> GetCategoryAttributes(int categoryId, CancellationToken ct)
    {
        await EnsureMarketplaceAsync(ct);
        return Ok(await _listings.GetCategoryAttributesAsync(categoryId, ct));
    }

    [HttpGet("trendyol/brands")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<IReadOnlyList<MarketplaceBrand>>> SearchBrands([FromQuery] string query, CancellationToken ct)
    {
        await EnsureMarketplaceAsync(ct);
        return Ok(await _listings.SearchBrandsAsync(query ?? "", ct));
    }

    [HttpGet("trendyol/cargo-providers")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<IReadOnlyList<MarketplaceCargoProvider>>> GetCargoProviders(CancellationToken ct)
    {
        await EnsureMarketplaceAsync(ct);
        return Ok(await _listings.GetCargoProvidersAsync(ct));
    }

    [HttpPost("listings/{productId:guid}/create")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<MarketplaceListingDto>> CreateListingOnMarketplace(Guid productId, SubmitListingRequest req, CancellationToken ct)
    {
        await EnsureMarketplaceAsync(ct);
        return Ok(await _listings.SubmitListingAsync(productId, req, ct));
    }

    // ---- Çekilen siparişler ----
    // Sipariş listesi finansal kayıttır → /pazaryeri-siparisleri ekranıyla aynı FINANCE rolleri (alıcı adı PII + iç hata detayı).
    [HttpGet("orders")]
    [Authorize(Roles = "Owner,Admin,Accountant")]
    public async Task<ActionResult<PagedResult<MarketplaceOrderDto>>> GetOrders([FromQuery] MarketplaceOrderQuery query, CancellationToken ct)
    {
        await EnsureMarketplaceAsync(ct);
        return Ok(await _orders.GetAsync(query, ct));
    }

    /// <summary>Bildirim çanı: son 24 saatteki pazaryeri siparişleri. Yetki yoksa boş döner (çan hata vermesin).</summary>
    [HttpGet("orders/pending")]
    public async Task<ActionResult<IReadOnlyList<MarketplaceOrderDto>>> GetPending(CancellationToken ct)
    {
        var e = await _entitlements.GetAsync(ct);
        if (!e.MarketplaceIntegration) return Ok(Array.Empty<MarketplaceOrderDto>());
        return Ok(await _orders.GetPendingAsync(ct));
    }
}
