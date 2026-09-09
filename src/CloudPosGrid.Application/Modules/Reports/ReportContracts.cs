namespace CloudPosGrid.Application.Modules.Reports;

/// <summary>Ad + tutar + adet (ödeme yöntemi / gider kategorisi kırılımları için).</summary>
public record NamedAmountDto(string Name, decimal Amount, int Count);

public record CategoryBreakdownDto(string Category, decimal Quantity, decimal Total);

public record TopProductReportDto(Guid ProductId, string Name, decimal Quantity, decimal Total);

/// <param name="Total">Günün brüt cirosu (KDV DAHİL, fatura GrandTotal toplamı) — rapor grafiği bunu çizer.</param>
/// <param name="NetTotal">Günün NET cirosu (KDV hariç, kısmi iade düşülmüş, puan indirimi çıkarılmış).
/// Asistanın "ciro" sözleşmesi KDV hariçtir (özet/karşılaştırma/en-çok-satan hep net konuşur); tahmin de
/// bu alanı kullanır ki aynı dönem için iki farklı rakam söylenmesin.</param>
public record DailySalesDto(DateTime Date, decimal Total, int Count, decimal NetTotal = 0m);

/// <summary>Satış raporu — seçili tarih aralığı için özet, kırılımlar ve trend.</summary>
public record SalesReportDto(
    int SalesCount,
    decimal SalesTotal,
    decimal SalesSubtotal,
    decimal VatTotal,
    decimal AvgBasket,
    decimal EstimatedProfit,
    List<NamedAmountDto> ByPaymentMethod,
    List<CategoryBreakdownDto> ByCategory,
    List<TopProductReportDto> TopProducts,
    List<DailySalesDto> DailyTrend);

/// <summary>Finansal rapor — gelir/gider/net ve kategori kırılımları.</summary>
public record FinancialReportDto(
    decimal Income,
    decimal Expense,
    decimal Net,
    List<NamedAmountDto> IncomeByCategory,
    List<NamedAmountDto> ExpenseByCategory);

/// <summary>Kâr-Zarar: bir kanal (Mağaza/Trendyol) ya da kategori için ciro/maliyet/kâr/marj.</summary>
public record ProfitBreakdownDto(string Name, decimal Revenue, decimal Cost, decimal Profit, decimal MarginPercent);

/// <summary>Ürün bazında kâr (ciro − COGS). Puan indirimi/pazaryeri kesintisi ürün seviyesine
/// dağıtılamadığından bu YAKLAŞIK (kesinti öncesi) brüt üründür — ürünleri kârlılığa göre SIRALAMAK için.</summary>
public record ProductProfitDto(Guid ProductId, string Name, decimal Quantity, decimal Revenue, decimal Cost, decimal Profit, decimal MarginPercent);

public record ProfitTrendPointDto(DateTime Date, decimal Profit);

/// <summary>Kâr-Zarar raporu — dönem geneli brüt kâr + kanal/kategori kırılımı + günlük kâr trendi.
/// Ciro KDV hariç (net); maliyet satış anındaki alış maliyeti (UnitCost), yoksa güncel alış fiyatı.</summary>
public record ProfitReportDto(
    decimal TotalRevenue,
    decimal TotalCost,
    decimal GrossProfit,
    decimal GrossMarginPercent,
    List<ProfitBreakdownDto> ByChannel,
    List<ProfitBreakdownDto> ByCategory,
    List<ProfitTrendPointDto> Trend);

/// <summary>Kasa hesabı bazında gün içi giriş/çıkış ve güncel bakiye (gün sonu sayım karşılaştırması için).</summary>
public record CashAccountCloseDto(string Name, decimal In, decimal Out, decimal Balance);

/// <summary>Gün sonu (Z) raporu — tek günün satış, tahsilat ve kasa özeti.</summary>
public record DailyCloseDto(
    DateTime Date,
    int SalesCount,
    decimal SalesTotal,
    decimal SalesSubtotal,
    decimal VatTotal,
    List<NamedAmountDto> ByPaymentMethod,
    decimal Income,
    decimal Expense,
    decimal Net,
    List<CashAccountCloseDto> CashAccounts,
    int OpenOrdersCount,
    decimal OpenOrdersTotal);

// ---- Cari yaşlandırma (aging) ----
/// <summary>Tek carinin yaşlandırma satırı: bakiye + yaş kovaları (borcun kaç gün eskidiği).</summary>
public record ContactAgingDto(
    Guid ContactId, string Name, string Type, string? Phone,
    decimal Balance, decimal Current, decimal D31_60, decimal D61_90, decimal Over90, int? OldestDays);

/// <summary>Cari yaşlandırma raporu: alacaklar (bize borçlu) ve borçlar (bizim borçlu olduğumuz) yaşa göre kovalanır.
/// FIFO: ödemeler en eski borçları kapatır → kalan bakiye en yeni hareketlerden oluşur.</summary>
public record AgingReportDto(
    DateTime AsOf,
    decimal TotalReceivable, decimal TotalPayable,
    decimal Current, decimal D31_60, decimal D61_90, decimal Over90,
    List<ContactAgingDto> Receivables, List<ContactAgingDto> Payables);

// ---- Fire / zayi raporu ----
public record WasteItemDto(Guid ProductId, string ProductName, decimal Quantity, decimal Cost);
/// <summary>Dönemsel fire: toplam maliyet + ürün ve neden kırılımı (kâr raporunda görünmeyen gizli kayıp).</summary>
public record WasteReportDto(decimal TotalCost, decimal TotalQuantity, List<WasteItemDto> Items, List<NamedAmountDto> ByReason);

/// <summary>Saatlik satış ısı haritası hücresi (yerel saate göre): hafta günü (0=Pzt..6=Paz) × saat (0-23).</summary>
public record HourlySalesCellDto(int Weekday, int Hour, int Count, decimal Revenue);

/// <summary>Personel bazlı satış (SellerUserId'ye göre) — ad istemcide personel listesinden eşlenir.</summary>
public record StaffSalesDto(Guid? SellerUserId, int SalesCount, decimal Revenue, decimal AvgBasket);

public interface IReportService
{
    Task<SalesReportDto> GetSalesAsync(DateTime from, DateTime to, CancellationToken ct = default);
    Task<FinancialReportDto> GetFinancialAsync(DateTime from, DateTime to, CancellationToken ct = default);
    Task<ProfitReportDto> GetProfitAsync(DateTime from, DateTime to, CancellationToken ct = default);
    /// <summary>Ürün bazında brüt kâr (ciro − COGS), kâra göre azalan; kesinti öncesi yaklaşık sıralama.</summary>
    Task<List<ProductProfitDto>> GetProductProfitAsync(DateTime from, DateTime to, CancellationToken ct = default);
    Task<DailyCloseDto> GetDailyCloseAsync(DateTime date, CancellationToken ct = default);
    Task<AgingReportDto> GetAgingAsync(CancellationToken ct = default);
    /// <summary>Dönemsel fire/zayi maliyeti (Reference=Waste hareketleri).</summary>
    Task<WasteReportDto> GetWasteAsync(DateTime from, DateTime to, CancellationToken ct = default);
    /// <summary>Saatlik satış ısı haritası — yoğun saat/gün tespiti (personel planlaması).</summary>
    Task<List<HourlySalesCellDto>> GetHourlySalesAsync(DateTime from, DateTime to, CancellationToken ct = default);
    /// <summary>Personel bazlı satış (ciro/adet/ort. sepet) — prim ve performans için.</summary>
    Task<List<StaffSalesDto>> GetStaffSalesAsync(DateTime from, DateTime to, CancellationToken ct = default);
}
