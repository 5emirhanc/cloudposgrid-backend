using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Domain.Entities;

/// <summary>Stok hareketi (giriş/çıkış/düzeltme). current_stock bu hareketlerle güncellenir.</summary>
public class StockMovement : BaseEntity
{
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public StockMovementType Type { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }

    public StockMovementReference Reference { get; set; } = StockMovementReference.Manual;
    public Guid? RefId { get; set; }
    public string? Note { get; set; }

    /// <summary>Fire/zayi nedeni — yalnız Reference=Waste hareketlerinde dolu (fire raporunda kırılım için).</summary>
    public WasteReason? WasteReason { get; set; }

    /// <summary>Hareket sonrası stok bakiyesi (denetim için).</summary>
    public decimal StockAfter { get; set; }
    public Guid? CreatedBy { get; set; }

    /// <summary>Hareketin ait olduğu şube (çok-şube tam stok). Null = eski/şubesiz hareket (varsayılan şubeye sayılır).
    /// İptal/iade ters kaydında hangi şubenin stoğuna döneceğini bilmek için gerekir.</summary>
    public Guid? BranchId { get; set; }
}
