using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Domain.Entities;

/// <summary>Kasa veya banka hesabı.</summary>
public class CashAccount : BaseEntity
{
    public string Name { get; set; } = null!;
    public CashAccountType Type { get; set; } = CashAccountType.Cash;
    public decimal Balance { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Bağlı olduğu şube (çok şube). Tek şubeli işletmelerde varsayılan şube.</summary>
    public Guid? BranchId { get; set; }
}
