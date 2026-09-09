using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>Ürün kategorisi (tenant şemasında).</summary>
public class Category : BaseEntity
{
    public string Name { get; set; } = null!;
    public Guid? ParentId { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<Product> Products { get; set; } = new List<Product>();
}
