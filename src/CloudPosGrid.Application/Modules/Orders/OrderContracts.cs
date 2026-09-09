using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Application.Modules.Orders;

// ---- Bölge / Masa ----
public record ServiceAreaDto(Guid Id, string Name, int SortOrder, bool IsActive);
public record CreateServiceAreaRequest(string Name, int SortOrder);
public record UpdateServiceAreaRequest(string Name, int SortOrder, bool IsActive);

public record DiningTableDto(
    Guid Id, string Name, Guid? AreaId, string? AreaName, int SortOrder, bool IsActive,
    Guid? OpenOrderId, decimal OpenTotal);
public record CreateDiningTableRequest(string Name, Guid? AreaId, int SortOrder);
public record UpdateDiningTableRequest(string Name, Guid? AreaId, int SortOrder, bool IsActive);

// ---- Adisyon ----
public record OrderLineDto(
    Guid Id, Guid ProductId, string ProductName, decimal Quantity,
    decimal UnitPrice, decimal VatRate, decimal LineTotal, string? Note);

public record OrderDto(
    Guid Id, OrderType Type, OrderStatus Status, OrderSource Source, Guid? TableId, string? TableName,
    Guid? ContactId, string? Label, string? Note, string? AssetInfo, WorkStatus? WorkStatus, DateTime OpenedAt,
    decimal Subtotal, decimal VatTotal, decimal GrandTotal, Guid? InvoiceId,
    IReadOnlyList<OrderLineDto> Lines);

public record OrderListItemDto(
    Guid Id, OrderType Type, OrderStatus Status, OrderSource Source, Guid? TableId, string? TableName,
    string? Label, string? AssetInfo, WorkStatus? WorkStatus, DateTime OpenedAt, decimal GrandTotal, int LineCount);

public record OpenOrderRequest(OrderType Type, Guid? TableId, Guid? ContactId, string? Label, string? Note, string? AssetInfo = null);
public record AddOrderLineRequest(Guid ProductId, decimal Quantity, string? Note);
public record UpdateOrderLineRequest(decimal Quantity);
public record UpdateWorkStatusRequest(WorkStatus Status);

public record OrderPaymentRequest(Guid CashAccountId, decimal Amount, PaymentMethod Method);
// ContactId: veresiye/kısmi ödemede borcun yazılacağı cari. Tip: bahşiş (faturaya dahil değil).
// Payments: çoklu/karma ödeme (split tender) — doluysa tek Payment yerine bu liste faturaya işlenir.
public record CloseOrderRequest(OrderPaymentRequest? Payment, Guid? ContactId = null, decimal Tip = 0,
    // Elle indirim — faturaya aynen aktarılır (yetki/limit denetimi InvoiceService'te).
    decimal? ManualDiscountPercent = null, decimal? ManualDiscountAmount = null, DiscountReason? ManualDiscountReason = null,
    List<OrderPaymentRequest>? Payments = null);

// Adisyon bölme: seçilen satır/miktarlar ayrı bir satışa dönüşür, kalan adisyonda kalır.
public record SplitCloseItem(Guid LineId, decimal Quantity);
// İndirim yalnız BÖLÜNEN kısma uygulanır; adisyonda kalan satırlar indirimsiz devam eder.
public record SplitCloseRequest(List<SplitCloseItem> Items, OrderPaymentRequest? Payment, Guid? ContactId = null,
    decimal? ManualDiscountPercent = null, decimal? ManualDiscountAmount = null, DiscountReason? ManualDiscountReason = null,
    List<OrderPaymentRequest>? Payments = null);

/// <summary>Adisyonu başka masaya taşı. Hedef masada AÇIK adisyon varsa (Merge=true) satırlar oraya birleştirilir.</summary>
public record MoveOrderRequest(Guid TargetTableId, bool Merge = false);

// QR menüden müşteri siparişi (public)
public record PlacePublicOrderItem(Guid ProductId, decimal Quantity, string? Note);
public record PlacePublicOrderRequest(OrderType Type, Guid? TableId, string? Label, List<PlacePublicOrderItem> Items);

// Pazaryeri (Trendyol) siparişi — senkron servisi barkodları ürünlere eşleyip bu isteği kurar.
// Fiyat pazaryerinden gelir (gerçek satış tutarı). Cari "{Channel} (Pazaryeri)" olarak servis içinde çözülür.
public record MarketplaceOrderLine(Guid ProductId, string ProductName, decimal Quantity, decimal UnitPrice, decimal VatRate);
public record PlaceMarketplaceOrderRequest(string Channel, string OrderNumber, string? BuyerName, List<MarketplaceOrderLine> Lines);

// ---- Servis arayüzleri ----
public interface IDiningService
{
    Task<List<ServiceAreaDto>> GetAreasAsync(CancellationToken ct = default);
    Task<ServiceAreaDto> CreateAreaAsync(CreateServiceAreaRequest req, CancellationToken ct = default);
    Task<ServiceAreaDto> UpdateAreaAsync(Guid id, UpdateServiceAreaRequest req, CancellationToken ct = default);
    Task DeleteAreaAsync(Guid id, CancellationToken ct = default);

    Task<List<DiningTableDto>> GetTablesAsync(CancellationToken ct = default);
    Task<DiningTableDto> CreateTableAsync(CreateDiningTableRequest req, CancellationToken ct = default);
    Task<DiningTableDto> UpdateTableAsync(Guid id, UpdateDiningTableRequest req, CancellationToken ct = default);
    Task DeleteTableAsync(Guid id, CancellationToken ct = default);
}

public interface IOrderService
{
    Task<List<OrderListItemDto>> GetOpenAsync(CancellationToken ct = default);
    Task<OrderDto> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<OrderDto> OpenAsync(OpenOrderRequest req, CancellationToken ct = default);
    Task<OrderDto> AddLineAsync(Guid orderId, AddOrderLineRequest req, CancellationToken ct = default);
    Task<OrderDto> UpdateLineAsync(Guid orderId, Guid lineId, UpdateOrderLineRequest req, CancellationToken ct = default);
    Task<OrderDto> RemoveLineAsync(Guid orderId, Guid lineId, CancellationToken ct = default);
    Task CancelAsync(Guid id, CancellationToken ct = default);
    Task<OrderDto> SetWorkStatusAsync(Guid id, WorkStatus status, CancellationToken ct = default);
    Task<OrderDto> CloseAsync(Guid id, CloseOrderRequest req, CancellationToken ct = default);
    Task<OrderDto> SplitCloseAsync(Guid id, SplitCloseRequest req, CancellationToken ct = default);
    /// <summary>Adisyonu başka masaya taşır; hedefte açık adisyon varsa Merge=true ile satırlar birleştirilir
    /// (kaynak adisyon iptal edilir, dönen DTO hedef adisyondur).</summary>
    Task<OrderDto> MoveAsync(Guid id, MoveOrderRequest req, CancellationToken ct = default);
    Task<OrderDto> PlacePublicAsync(PlacePublicOrderRequest req, CancellationToken ct = default);
    /// <summary>Pazaryeri siparişini satışa çevirir: Order(Source=Marketplace) + veresiye satış faturası
    /// (kanal carisine borç, stok düşümü). Fatura tutarı pazaryeri fiyatlarından oluşur.</summary>
    Task<OrderDto> PlaceMarketplaceAsync(PlaceMarketplaceOrderRequest req, CancellationToken ct = default);
}
