using CloudPosGrid.Application.Modules.Purchasing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Ürün-tedarikçi eşlemesi: bir ürünün hangi tedarikçilerden hangi koşullarla alındığı.
/// Geri ofis/satın alma verisi olduğundan yalnız Owner/Admin erişir.
/// </summary>
[ApiController]
[Route("api/product-suppliers")]
[Authorize(Roles = "Owner,Admin")]
public class ProductSuppliersController : ControllerBase
{
    private readonly IProductSupplierService _service;

    public ProductSuppliersController(IProductSupplierService service) => _service = service;

    /// <summary>Bir ürünün tedarikçi eşlemelerini getirir (tedarikçi adı dahil).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProductSupplierDto>>> Get([FromQuery] Guid productId, CancellationToken ct)
        => Ok(await _service.ListByProductAsync(productId, ct));

    /// <summary>Bir ürünün tedarikçi eşlemelerinin tamamını değiştirir (sil-yeniden kur).</summary>
    [HttpPut]
    public async Task<ActionResult<IReadOnlyList<ProductSupplierDto>>> Upsert([FromQuery] Guid productId, UpsertProductSuppliersRequest req, CancellationToken ct)
        => Ok(await _service.UpsertAsync(productId, req, ct));

    /// <summary>Tek bir tedarikçi eşlemesini siler.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _service.DeleteAsync(id, ct);
        return NoContent();
    }
}
