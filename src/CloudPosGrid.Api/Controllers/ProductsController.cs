using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Recipes;
using CloudPosGrid.Application.Modules.Stock;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

[ApiController]
[Route("api/products")]
[Authorize]
public class ProductsController : ControllerBase
{
    private readonly IProductService _service;
    private readonly IReplenishmentService _replenishment;
    private readonly IPlanEntitlementProvider _entitlements;
    private readonly IRecipeService _recipes;
    private readonly IProductOptionService _options;

    public ProductsController(IProductService service, IReplenishmentService replenishment, IPlanEntitlementProvider entitlements, IRecipeService recipes, IProductOptionService options)
    {
        _service = service;
        _replenishment = replenishment;
        _entitlements = entitlements;
        _recipes = recipes;
        _options = options;
    }

    /// <summary>Ürünün opsiyonları (az şekerli / ekstra shot / boy) — POS satışta gösterilir.</summary>
    [HttpGet("{id:guid}/options")]
    public async Task<ActionResult<IReadOnlyList<ProductOptionDto>>> GetOptions(Guid id, CancellationToken ct)
        => Ok(await _options.ListByProductAsync(id, ct));

    /// <summary>Ürünün opsiyonlarını ayarlar (tümünü değiştirir).</summary>
    [HttpPut("{id:guid}/options")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<IReadOnlyList<ProductOptionDto>>> SetOptions(Guid id, SaveProductOptionsRequest req, CancellationToken ct)
        => Ok(await _options.SaveAsync(id, req, ct));

    /// <summary>Ürünün reçetesi (bileşenleri) — bileşik ürün satışında bunlar stoktan düşer.</summary>
    [HttpGet("{id:guid}/recipe")]
    public async Task<ActionResult<RecipeDto>> GetRecipe(Guid id, CancellationToken ct)
        => Ok(await _recipes.GetAsync(id, ct));

    /// <summary>Ürünün reçetesini ayarlar (tüm bileşenleri değiştirir).</summary>
    [HttpPut("{id:guid}/recipe")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<RecipeDto>> SetRecipe(Guid id, SetRecipeRequest req, CancellationToken ct)
        => Ok(await _recipes.SetAsync(id, req, ct));

    [HttpGet]
    public async Task<ActionResult<PagedResult<ProductDto>>> Get([FromQuery] ProductQuery query, CancellationToken ct)
        => Ok(await _service.GetAsync(query, ct));

    [HttpGet("low-stock")]
    public async Task<ActionResult<List<ProductDto>>> GetLowStock(CancellationToken ct)
        => Ok(await _service.GetLowStockAsync(ct));

    /// <summary>Akıllı stok tahminleme: satış hızına göre horizonDays içinde tükenecek ürünler + önerilen sipariş miktarı.</summary>
    [HttpGet("replenishment")]
    public async Task<ActionResult<List<ReplenishmentItemDto>>> GetReplenishment(
        [FromQuery] int windowDays = 30, [FromQuery] int horizonDays = 7, [FromQuery] int coverDays = 30, CancellationToken ct = default)
    {
        var e = await _entitlements.GetAsync(ct);
        if (!e.SmartReplenishment)
            throw new PlanUpgradeException("Akıllı sipariş önerisi Zincir pakete özeldir. Kullanmak için paketinizi yükseltin.");
        return Ok(await _replenishment.GetAsync(windowDays, horizonDays, coverDays, ct));
    }

    [HttpGet("barcode/{barcode}")]
    public async Task<ActionResult<ProductDto>> GetByBarcode(string barcode, CancellationToken ct)
    {
        var product = await _service.GetByBarcodeAsync(barcode, ct);
        return product is null ? NotFound() : Ok(product);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProductDto>> GetById(Guid id, CancellationToken ct)
        => Ok(await _service.GetByIdAsync(id, ct));

    [HttpGet("{id:guid}/movements")]
    public async Task<ActionResult<List<StockMovementDto>>> GetMovements(Guid id, CancellationToken ct)
        => Ok(await _service.GetProductMovementsAsync(id, ct));

    [HttpPost]
    public async Task<ActionResult<ProductDto>> Create(CreateProductRequest req, CancellationToken ct)
        => Ok(await _service.CreateAsync(req, ct));

    /// <summary>Varyantlı ürün oluşturur (butik beden/renk): parent şablon + her varyant ayrı ürün.</summary>
    [HttpPost("with-variants")]
    public async Task<ActionResult<ProductWithVariantsDto>> CreateWithVariants(CreateProductWithVariantsRequest req, CancellationToken ct)
        => Ok(await _service.CreateWithVariantsAsync(req, ct));

    /// <summary>Toplu fiyat güncelleme (zam). Geniş etkili → Owner/Admin. Preview=true yalnız hesaplar.</summary>
    /// <summary>POS okutma: terazi barkodunu çözüp ürün + ondalık miktar/gömülü fiyat döner.
    /// Terazi barkodu değilse normal ürün (miktar 1). Kasiyer de okutur → rol kısıtı yok.</summary>
    [HttpGet("scan/{code}")]
    public async Task<ActionResult<ScanResultDto>> Scan(string code, CancellationToken ct)
    {
        var r = await _service.ScanAsync(code, ct);
        return r is null ? NotFound() : Ok(r);
    }

    [HttpPost("bulk-price")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<BulkPriceResultDto>> BulkPrice(BulkPriceUpdateRequest req, CancellationToken ct)
        => Ok(await _service.BulkUpdatePricesAsync(req, ct));

    [HttpPost("import")]
    public async Task<ActionResult<ImportResultDto>> Import(ImportProductsRequest req, CancellationToken ct)
        => Ok(await _service.ImportAsync(req, ct));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ProductDto>> Update(Guid id, UpdateProductRequest req, CancellationToken ct)
        => Ok(await _service.UpdateAsync(id, req, ct));

    /// <summary>Barkodsuz ürüne benzersiz dahili EAN-13 barkod üretir (etiket + POS okutma için).</summary>
    [HttpPost("{id:guid}/generate-barcode")]
    public async Task<ActionResult<ProductDto>> GenerateBarcode(Guid id, CancellationToken ct)
        => Ok(await _service.GenerateBarcodeAsync(id, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _service.DeleteAsync(id, ct);
        return NoContent();
    }
}
