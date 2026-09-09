namespace CloudPosGrid.Application.Modules.Analytics;

/// <summary>Envanter değerleme + stok devir hızı özeti. ŞUBE FARKINDA: bir şube seçiliyse (şubeye kilitli
/// kullanıcıda zorunlu) rakamlar o şubenin ProductBranchStock bakiyesinden ve o şubenin satışlarından
/// hesaplanır; şube seçili değilse (kısıtsız Owner) tenant geneli toplamdır.</summary>
public record InventoryValuationDto(
    decimal TotalValue,       // Σ (CurrentStock × alış fiyatı) — aktif, varyant-şablonu olmayan ürünler
    decimal TotalUnits,       // Σ CurrentStock (toplam elde miktar)
    int ProductCount,         // değerlemeye giren ve elde stoğu olan (CurrentStock>0) ürün sayısı
    decimal PeriodCogs,       // dönem içinde satılan malın maliyeti (COGS) — devir hızı için
    decimal TurnoverRate,     // devir sayısı: PeriodCogs / TotalValue (dönem içinde stok kaç kez döndü)
    decimal DaysOfInventory); // ortalama stok bekleme süresi (gün) = days / TurnoverRate

/// <summary>ABC (Pareto) sınıflandırma satırı: ürünün dönem satış cirosu (net, KDV hariç), kümülatif ciro payı
/// ve sınıfı. A = ilk %80, B = %80-95 arası, C = kalan.</summary>
public record AbcItemDto(
    Guid ProductId,
    string Name,
    decimal Revenue,
    decimal CumulativePercent,
    string Class);

/// <summary>Ölü stok satırı: dönem içinde hiç satılmamış ama elde stoğu bulunan ürün + bağlı sermaye
/// (CurrentStock × alış fiyatı) + son satıştan bu yana geçen gün (hiç satılmadıysa null).</summary>
public record DeadStockItemDto(
    Guid ProductId,
    string Name,
    decimal CurrentStock,
    decimal TiedCapital,
    int? DaysSinceLastSale);

/// <summary>Envanter analitiği birleşik sonucu (tek çağrı): değerleme + devir hızı, ABC sınıflandırma, ölü stok.</summary>
public record InventoryAnalyticsDto(
    int Days,
    InventoryValuationDto Valuation,
    List<AbcItemDto> Abc,
    List<DeadStockItemDto> DeadStock,
    decimal DeadStockValue);

public interface IInventoryAnalyticsService
{
    /// <summary>Değerleme + ABC + ölü stok birleşik sonucu. days = analiz penceresi (gün), [1..365].</summary>
    Task<InventoryAnalyticsDto> GetAsync(int days = 90, CancellationToken ct = default);
    /// <summary>Toplam stok değeri + toplam miktar + stok devir hızı (son N gün COGS'una göre).</summary>
    Task<InventoryValuationDto> GetValuationAsync(int days = 90, CancellationToken ct = default);
    /// <summary>ABC (Pareto) sınıflandırma: ürünler son N günün satış cirosuna göre A/B/C sınıfına ayrılır.</summary>
    Task<List<AbcItemDto>> GetAbcAsync(int days = 90, CancellationToken ct = default);
    /// <summary>Ölü stok: son N günde hiç satılmamış ama elde stoğu olan ürünler + bağlı sermaye.</summary>
    Task<List<DeadStockItemDto>> GetDeadStockAsync(int days = 90, CancellationToken ct = default);
}
