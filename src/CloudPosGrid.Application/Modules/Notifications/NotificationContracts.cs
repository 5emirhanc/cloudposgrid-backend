namespace CloudPosGrid.Application.Modules.Notifications;

public record NotificationDto(
    Guid Id,
    string Type,
    string Severity,
    string Title,
    string Message,
    string? Link,
    string? Icon,
    bool IsRead,
    DateTime CreatedAt);

public record NotificationListDto(IReadOnlyList<NotificationDto> Items, int UnreadCount);

/// <summary>Bir bildirim oluşturma isteği — diğer servisler/tarayıcılar bu tohumla zile uyarı düşürür.</summary>
public record RaiseNotificationRequest(
    string Type,
    string Title,
    string Message,
    string Severity = "info",
    string? Link = null,
    string? Icon = null,
    Guid? BranchId = null,
    string? DedupKey = null);

public interface INotificationService
{
    Task<NotificationListDto> ListAsync(bool unreadOnly, int take, CancellationToken ct = default);
    Task<int> UnreadCountAsync(CancellationToken ct = default);
    Task MarkReadAsync(Guid id, CancellationToken ct = default);
    Task MarkAllReadAsync(CancellationToken ct = default);
    Task DismissAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Bir bildirim oluşturur. <paramref name="req"/>.DedupKey verilirse ve aynı anahtarlı OKUNMAMIŞ bir
    /// bildirim zaten varsa yeni satır açmaz (idempotent) → tekrar eden tarama uyarıyı çoğaltmaz.
    /// Oluşturulduysa yeni Id, deduplike edildiyse null döner.
    /// </summary>
    Task<Guid?> RaiseAsync(RaiseNotificationRequest req, CancellationToken ct = default);
}
