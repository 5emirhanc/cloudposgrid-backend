namespace CloudPosGrid.Application.Abstractions;

/// <summary>
/// İşletme-içi denetim izi. Kaydı yalnızca EKLER (Added); çağıranın mevcut SaveChanges'i ile
/// aynı işlemde kalıcılaşır → eylem geri alınırsa iz de yazılmaz (tutarlı).
/// </summary>
public interface IAuditTrail
{
    void Record(string action, string? targetType = null, Guid? targetId = null, string? details = null);
}
