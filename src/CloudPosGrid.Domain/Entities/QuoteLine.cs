using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>Teklif kalemi. InvoiceLine'ın aynısı ama <c>UnitCost</c> YOKTUR:
/// teklif satılmış sayılmaz, kâr/maliyet raporuna girmez.</summary>
public class QuoteLine : BaseEntity
{
    public Guid QuoteId { get; set; }
    public Quote Quote { get; set; } = null!;

    /// <summary>Ürün zorunlu — dönüşümde faturaya ProductId geçmek gerekir. Serbest metin kalem için
    /// önce IsService=true bir hizmet ürünü tanımlanır (mevcut desen).</summary>
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>Ürün adı anlık kopyası — ürün sonradan yeniden adlandırılsa da teklif metni değişmez.</summary>
    public string ProductName { get; set; } = string.Empty;

    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal VatRate { get; set; }
    public decimal LineTotal { get; set; }
    public decimal VatAmount { get; set; }
}
