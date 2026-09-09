using CloudPosGrid.Domain.Common;

namespace CloudPosGrid.Domain.Entities;

/// <summary>
/// Ürün-tedarikçi eşlemesi: bir ürünün hangi tedarikçilerden (cari) tedarik edildiği ve her tedarikçiye
/// ait alım koşulları (tedarikçi stok kodu, son alış fiyatı, teslim süresi, minimum sipariş miktarı,
/// tercih bayrağı). Tenant geneli tutulur — şubeye bağlı DEĞİLDİR; satın alma kaynakları işletme
/// genelinde ortaktır. Bir üründe aynı tedarikçi yalnız bir kez bulunur (ProductId + ContactId benzersiz).
/// </summary>
public class ProductSupplier : BaseEntity
{
    /// <summary>Eşlenen ürün.</summary>
    public Guid ProductId { get; set; }

    /// <summary>Tedarikçi carisi (Contact — genelde Type = Supplier).</summary>
    public Guid ContactId { get; set; }

    /// <summary>Tedarikçinin bu ürün için kendi stok/katalog kodu (bizim SKU'muzdan farklı olabilir).</summary>
    public string? SupplierSku { get; set; }

    /// <summary>Bu tedarikçiden yapılan son alışın birim fiyatı (bilgi amaçlı; sipariş önerisinde kullanılır).</summary>
    public decimal LastPurchasePrice { get; set; }

    /// <summary>Sipariş verildikten kaç gün sonra malın geldiği (teslim/temin süresi) — tükenme hesabında kullanılır.</summary>
    public int LeadTimeDays { get; set; }

    /// <summary>Bu tedarikçinin kabul ettiği minimum sipariş miktarı (altında sipariş verilmez).</summary>
    public decimal MinOrderQuantity { get; set; }

    /// <summary>Tercih edilen (varsayılan) tedarikçi mi — otomatik sipariş önerisi bunu öne çıkarır.</summary>
    public bool IsPreferred { get; set; }
}
