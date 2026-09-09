using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Abstractions;

/// <summary>
/// Kiracıya (tenant) ait iş verisi için DbContext soyutlaması. Bu context'in bağlantısı
/// search_path ile ilgili tenant şemasına yönlendirilir; tablolar şema-bağımsız tanımlıdır.
/// </summary>
public interface IApplicationDbContext
{
    DbSet<Branch> Branches { get; }
    DbSet<Category> Categories { get; }
    DbSet<Product> Products { get; }
    DbSet<StockMovement> StockMovements { get; }
    DbSet<Contact> Contacts { get; }
    DbSet<AccountTransaction> AccountTransactions { get; }
    DbSet<CashAccount> CashAccounts { get; }
    DbSet<FinanceTransaction> FinanceTransactions { get; }
    /// <summary>Tekrarlayan gider şablonları (kira/maaş/abonelik) — her ay otomatik gider üretir.</summary>
    DbSet<RecurringExpense> RecurringExpenses { get; }
    /// <summary>Kasa vardiyaları (açılış/kapanış + kör sayım farkı).</summary>
    DbSet<CashShift> CashShifts { get; }
    DbSet<Invoice> Invoices { get; }
    DbSet<InvoiceLine> InvoiceLines { get; }
    DbSet<PurchaseOrder> PurchaseOrders { get; }
    DbSet<PurchaseOrderLine> PurchaseOrderLines { get; }
    DbSet<Quote> Quotes { get; }
    DbSet<QuoteLine> QuoteLines { get; }
    DbSet<Payment> Payments { get; }
    DbSet<TenantSettings> Settings { get; }
    DbSet<ServiceArea> ServiceAreas { get; }
    DbSet<DiningTable> DiningTables { get; }
    DbSet<Order> Orders { get; }
    DbSet<OrderLine> OrderLines { get; }
    DbSet<Appointment> Appointments { get; }
    /// <summary>İşletme-içi denetim izi (kim, ne zaman, neyi değiştirdi) — salt-ekle.</summary>
    DbSet<AuditEvent> AuditEvents { get; }
    DbSet<StockCountSession> StockCountSessions { get; }
    DbSet<StockCountSessionItem> StockCountSessionItems { get; }
    /// <summary>Şube başına ürün stoğu (çok-şube tam stok); Product.CurrentStock bunların toplamıdır.</summary>
    DbSet<ProductBranchStock> ProductBranchStocks { get; }
    DbSet<MarketplaceConnection> MarketplaceConnections { get; }
    DbSet<MarketplaceListing> MarketplaceListings { get; }
    DbSet<MarketplaceOrder> MarketplaceOrders { get; }
    /// <summary>AI Asistan'ın öğrendiği eğitim örnekleri (niyet + örnek soru) — motorun seed'ine eklenir.</summary>
    DbSet<AssistantTraining> AssistantTrainings { get; }
    /// <summary>AI Asistan'ın anlayamadığı sorular — admin niyete atayınca eğitime dönüşür.</summary>
    DbSet<AssistantUnresolved> AssistantUnresolveds { get; }
    /// <summary>Kalıcı bildirim merkezi (zil) — düşük stok, geciken alacak, anomali vb.</summary>
    DbSet<Notification> Notifications { get; }
    /// <summary>Reçete/ürün ağacı (BOM) satırları — bileşik ürün satışında bileşen stoğu düşer.</summary>
    DbSet<RecipeComponent> RecipeComponents { get; }
    /// <summary>Çek/senet portföyü — alınan/verilen kıymetli evrak, vade+durum takibi.</summary>
    DbSet<Cheque> Cheques { get; }
    /// <summary>Ürün-tedarikçi eşlemeleri (hangi ürün hangi tedarikçiden, alım koşullarıyla) — tenant geneli.</summary>
    DbSet<ProductSupplier> ProductSuppliers { get; }
    /// <summary>Genel dosya/ek metadata'sı — herhangi bir kayda (cari/fatura/ürün/sipariş/garanti) iliştirilen dosya URL'leri.</summary>
    DbSet<Attachment> Attachments { get; }
    /// <summary>Servis/garanti kayıtları — satılan ürünlerin garanti takibi.</summary>
    DbSet<WarrantyRecord> WarrantyRecords { get; }
    /// <summary>Referans (tavsiye) programı — tavsiye eden cari için benzersiz kod, ödül+durum takibi.</summary>
    DbSet<Referral> Referrals { get; }
    /// <summary>Hediye çeki — kesim, bakiye, harcama (redeem), iptal.</summary>
    DbSet<GiftCard> GiftCards { get; }
    /// <summary>Kural motoru (if-this-then-that) tanımları — AutomationEngine bunları periyodik değerlendirir.</summary>
    DbSet<AutomationRule> AutomationRules { get; }
    /// <summary>Personel prim/komisyon kuralları (yüzde oran; personele özel veya genel) — tenant geneli.</summary>
    DbSet<CommissionRule> CommissionRules { get; }
    /// <summary>Kampanya/promosyon kuralları (kategori/ürün %, happy hour, X al Y öde) — tenant geneli.</summary>
    DbSet<Campaign> Campaigns { get; }
    /// <summary>Ürün opsiyonları (az şekerli / ekstra shot / boy) — POS satışta fiyat farkı + not olarak uygulanır.</summary>
    DbSet<ProductOption> ProductOptions { get; }

    /// <summary>Tarayıcı Web Push abonelikleri (#6) — bildirim üretilince push gönderilecek cihazlar.</summary>
    DbSet<PushSubscription> PushSubscriptions { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Geçerli tenant şemasında verilen sayaç için bir sonraki değeri atomik olarak üretir
    /// (eşzamanlı isteklerde çift numara oluşmaz). Sayaç ilk kez oluşturuluyorsa
    /// <paramref name="seedIfNew"/> değerinden başlatılır.
    /// </summary>
    Task<long> NextDocumentNumberAsync(string key, long seedIfNew, CancellationToken ct = default);
}
