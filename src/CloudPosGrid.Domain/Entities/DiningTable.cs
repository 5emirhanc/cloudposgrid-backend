using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>Masa (hospitality). Bir bölgeye (ServiceArea) bağlı olabilir.</summary>
public class DiningTable : BaseEntity
{
    public string Name { get; set; } = null!;
    public Guid? AreaId { get; set; }
    public ServiceArea? Area { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? BranchId { get; set; }
}
