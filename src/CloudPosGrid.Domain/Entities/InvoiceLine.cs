using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>Fatura satırı. LineTotal KDV hariç tutardır; VatAmount ayrı tutulur.</summary>
public class InvoiceLine : BaseEntity
{
    public Guid InvoiceId { get; set; }
    public Invoice Invoice { get; set; } = null!;

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public string ProductName { get; set; } = null!;

    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal VatRate { get; set; }
    public decimal LineTotal { get; set; }
    public decimal VatAmount { get; set; }

    /// <summary>Satış anındaki ürün alış maliyeti (birim) — kesin kâr için sabitlenir. Alış fiyatı sonradan
    /// değişse bile geçmiş kâr doğru kalır. null = yakalanmamış (feature öncesi/legacy satır) → rapor güncel
    /// alış fiyatına düşer. 0 = gerçekten maliyetsiz ürün (yakalanmış, drift olmaz).</summary>
    public decimal? UnitCost { get; set; }

    /// <summary>Bu satırdan iade edilen toplam miktar (kısmi iade). İade edilebilir kalan = Quantity − RefundedQuantity.
    /// Raporlar net ciro/COGS'u (Quantity − RefundedQuantity) üzerinden hesaplar.</summary>
    public decimal RefundedQuantity { get; set; }
}
