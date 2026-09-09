namespace CloudPosGrid.Domain.Common;

/// <summary>Tüm entity'ler için ortak temel: Guid kimlik ve zaman damgaları.</summary>
public abstract class BaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
