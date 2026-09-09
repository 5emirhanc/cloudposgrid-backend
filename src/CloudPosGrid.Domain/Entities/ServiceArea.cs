using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>Salon/bölge (ör. "Bahçe", "İç Salon") — masaları gruplamak için.</summary>
public class ServiceArea : BaseEntity
{
    public string Name { get; set; } = null!;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? BranchId { get; set; }

    public ICollection<DiningTable> Tables { get; set; } = new List<DiningTable>();
}
