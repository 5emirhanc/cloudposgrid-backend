using System.Text.Json;
using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Npgsql;

namespace CloudPosGrid.Infrastructure.Persistence;

/// <summary>
/// Kiracıya ait iş verisi. Tablolar şema-bağımsız tanımlıdır; gerçek şema bağlantı
/// açılışında search_path ile (TenantSchemaConnectionInterceptor) belirlenir.
/// </summary>
public class AppDbContext : DbContext, IApplicationDbContext
{
    // Şubeye kilitli kullanıcının şubesi (branch_id claim'i). null = kısıtsız (Owner/yönetici,
    // arka plan işleri, kayıt akışı) → filtre kapalı, tüm şubeler görünür.
    private readonly Guid? _branchFilter;

    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser) : base(options)
    {
        _branchFilter = currentUser.AssignedBranchId;
    }

    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<AccountTransaction> AccountTransactions => Set<AccountTransaction>();
    public DbSet<CashAccount> CashAccounts => Set<CashAccount>();
    public DbSet<FinanceTransaction> FinanceTransactions => Set<FinanceTransaction>();
    public DbSet<RecurringExpense> RecurringExpenses => Set<RecurringExpense>();
    public DbSet<CashShift> CashShifts => Set<CashShift>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<QuoteLine> QuoteLines => Set<QuoteLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<TenantSettings> Settings => Set<TenantSettings>();
    public DbSet<ServiceArea> ServiceAreas => Set<ServiceArea>();
    public DbSet<DiningTable> DiningTables => Set<DiningTable>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<StockCountSession> StockCountSessions => Set<StockCountSession>();
    public DbSet<StockCountSessionItem> StockCountSessionItems => Set<StockCountSessionItem>();
    public DbSet<ProductBranchStock> ProductBranchStocks => Set<ProductBranchStock>();
    public DbSet<MarketplaceConnection> MarketplaceConnections => Set<MarketplaceConnection>();
    public DbSet<MarketplaceListing> MarketplaceListings => Set<MarketplaceListing>();
    public DbSet<MarketplaceOrder> MarketplaceOrders => Set<MarketplaceOrder>();
    public DbSet<AssistantTraining> AssistantTrainings => Set<AssistantTraining>();
    public DbSet<AssistantUnresolved> AssistantUnresolveds => Set<AssistantUnresolved>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<RecipeComponent> RecipeComponents => Set<RecipeComponent>();
    public DbSet<Cheque> Cheques => Set<Cheque>();
    public DbSet<ProductSupplier> ProductSuppliers => Set<ProductSupplier>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<WarrantyRecord> WarrantyRecords => Set<WarrantyRecord>();
    public DbSet<Referral> Referrals => Set<Referral>();
    public DbSet<GiftCard> GiftCards => Set<GiftCard>();
    public DbSet<AutomationRule> AutomationRules => Set<AutomationRule>();
    public DbSet<CommissionRule> CommissionRules => Set<CommissionRule>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<ProductOption> ProductOptions => Set<ProductOption>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

    protected override void ConfigureConventions(ModelConfigurationBuilder cb)
    {
        cb.Properties<decimal>().HavePrecision(18, 4);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Bilinçli olarak HasDefaultSchema KULLANILMIYOR -> search_path ile çözülür.

        b.Entity<Branch>(e =>
        {
            e.ToTable("branches");
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Address).HasMaxLength(300);
            e.Property(x => x.Phone).HasMaxLength(40);
        });

        b.Entity<Category>(e =>
        {
            e.ToTable("categories");
            e.Property(x => x.Name).HasMaxLength(150).IsRequired();
            e.HasIndex(x => x.Name);
        });

        b.Entity<Product>(e =>
        {
            e.ToTable("products");
            // Eşzamanlı stok güncellemelerinde kayıp güncelleme/aşırı satışı engeller: PostgreSQL'in
            // xmin sistem sütununu iyimser eşzamanlılık jetonu olarak kullanır. Aynı ürünü aynı anda
            // satan iki işlemden biri çakışır -> DbUpdateConcurrencyException -> 409 "tekrar deneyin"
            // (satır tamamen geri alınır; aşırı satış olmaz). xmin sistem sütunu olduğundan migration gerekmez.
            e.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
            e.Ignore(x => x.IsLowStock);
            e.Property(x => x.Sku).HasMaxLength(64).IsRequired();
            e.Property(x => x.Barcode).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Unit).HasMaxLength(20);
            e.Property(x => x.ShelfLocation).HasMaxLength(60);
            e.Property(x => x.StorageArea).HasMaxLength(60);
            e.Property(x => x.ImageUrl).HasMaxLength(500);
            e.Property(x => x.BrandName).HasMaxLength(100);
            e.Property(x => x.VariantValues).HasMaxLength(200);
            e.HasIndex(x => x.ParentProductId); // varyant gruplama sorguları için
            // Ek görseller (pazaryeri ilanı): List<string> → jsonb. Mutable koleksiyon olduğundan ValueComparer şart.
            var imageUrlsComparer = new ValueComparer<List<string>>(
                (a, b) => (a ?? new()).SequenceEqual(b ?? new()),
                v => v == null ? 0 : v.Aggregate(0, (h, s) => HashCode.Combine(h, s == null ? 0 : s.GetHashCode())),
                v => v == null ? new List<string>() : v.ToList());
            e.Property(x => x.ImageUrls)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => string.IsNullOrEmpty(v) ? new List<string>() : (JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>()),
                    imageUrlsComparer);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.HasIndex(x => x.Sku).IsUnique();
            e.HasIndex(x => x.Barcode);
            e.HasIndex(x => x.Name);
            e.HasOne(x => x.Category).WithMany(c => c.Products)
                .HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<StockMovement>(e =>
        {
            e.ToTable("stock_movements");
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Reference).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => x.ProductId);
            e.HasIndex(x => x.CreatedAt);
            e.HasOne(x => x.Product).WithMany(p => p.Movements)
                .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProductBranchStock>(e =>
        {
            e.ToTable("product_branch_stocks");
            // (ProductId, BranchId) benzersiz — ürün+şube başına tek bakiye satırı.
            e.HasIndex(x => new { x.ProductId, x.BranchId }).IsUnique();
            e.HasOne(x => x.Product).WithMany()
                .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            // Per-şube iyimser eşzamanlılık jetonu (xmin). Product.CurrentStock'un xmin'i çoğu mutasyonu serileştirir
            // AMA şube transferi (kaynak −q / hedef +q) Product'a NET SIFIR uygular → EF products UPDATE üretmez →
            // Product.xmin kontrol edilmez. Bu satır kendi jetonuyla, transfer ile eşzamanlı satış/transfer'in
            // aynı şube bakiyesini bozmasını (aşırı-satış / toplam≠parça) engeller. xmin sistem sütunu → migration gerekmez.
            e.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        });

        b.Entity<Contact>(e =>
        {
            e.ToTable("contacts");
            // İyimser eşzamanlılık: cari Balance (veresiye/alacak) okuma-değiştirme-yazma; aynı
            // cariye paralel iki işlem bakiyeyi bozmasın. Çakışma → 409 "tekrar deneyin".
            e.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.TaxOffice).HasMaxLength(120);
            e.Property(x => x.TaxNo).HasMaxLength(40);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.Tags).HasMaxLength(300);
            e.HasIndex(x => x.Name);
        });

        b.Entity<AccountTransaction>(e =>
        {
            e.ToTable("account_transactions");
            e.Property(x => x.Direction).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.DocRef).HasMaxLength(80);
            e.HasIndex(x => x.ContactId);
            e.HasIndex(x => x.Date);
            e.HasOne(x => x.Contact).WithMany(c => c.Transactions)
                .HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CashAccount>(e =>
        {
            e.ToTable("cash_accounts");
            // Şube izolasyonu: kısıtlı kullanıcı yalnız kendi şubesinin (veya şube-bağımsız null) kasasını
            // bulabilir → başka şubenin kasasına yazma (foreign CashAccountId) da otomatik engellenir (NotFound).
            e.HasQueryFilter(x => _branchFilter == null || x.BranchId == null || x.BranchId == _branchFilter);
            // İyimser eşzamanlılık: Balance okuma-değiştirme-yazma olduğundan, aynı kasaya paralel
            // iki hareket birbirini ezip bakiyeyi kaydırabilir. xmin ile çakışan işlem geri alınır
            // (409 "tekrar deneyin") → kasa/gün sonu bakiyesi tutarlı kalır. Migration gerekmez.
            e.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.BranchId);
        });

        b.Entity<FinanceTransaction>(e =>
        {
            e.ToTable("finance_transactions");
            e.HasQueryFilter(x => _branchFilter == null || x.BranchId == _branchFilter); // şube izolasyonu
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.PaymentMethod).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Category).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(500);
            e.HasIndex(x => x.Date);
            e.HasIndex(x => x.BranchId);
            e.HasOne(x => x.CashAccount).WithMany()
                .HasForeignKey(x => x.CashAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<RecurringExpense>(e =>
        {
            // Tenant geneli şablon (şube filtresi yok); üretilen gider FinanceTransaction'ı kasa şubesine düşer.
            e.ToTable("recurring_expenses");
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Category).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.LastPostedPeriod).HasMaxLength(7);
            e.HasOne<CashAccount>().WithMany()
                .HasForeignKey(x => x.CashAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<CashShift>(e =>
        {
            e.ToTable("cash_shifts");
            e.HasQueryFilter(x => _branchFilter == null || x.BranchId == null || x.BranchId == _branchFilter); // şube izolasyonu
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.OpenedByName).HasMaxLength(160);
            e.Property(x => x.ClosedByName).HasMaxLength(160);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => new { x.CashAccountId, x.Status });
            e.HasOne(x => x.CashAccount).WithMany()
                .HasForeignKey(x => x.CashAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            // Şube izolasyonu: kısıtlı yönetici başka şubenin finansal izini (fatura no + tutar) görmesin.
            e.HasQueryFilter(x => _branchFilter == null || x.BranchId == _branchFilter);
            e.Property(x => x.Action).HasMaxLength(60).IsRequired();
            e.Property(x => x.ActorEmail).HasMaxLength(256);
            e.Property(x => x.TargetType).HasMaxLength(40);
            e.Property(x => x.Details).HasMaxLength(500);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.BranchId);
        });

        b.Entity<StockCountSession>(e =>
        {
            e.ToTable("stock_count_sessions");
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.CreatedByName).HasMaxLength(256);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<StockCountSessionItem>(e =>
        {
            e.ToTable("stock_count_session_items");
            e.HasOne(x => x.Session).WithMany(s => s.Items)
                .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.SessionId);
        });

        b.Entity<Invoice>(e =>
        {
            e.ToTable("invoices");
            e.HasQueryFilter(x => _branchFilter == null || x.BranchId == _branchFilter); // şube izolasyonu
            e.Property(x => x.Number).HasMaxLength(40).IsRequired();
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.Property(x => x.Channel).HasMaxLength(40).IsRequired();
            e.Property(x => x.ClientSaleId).HasMaxLength(64);
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.Date);
            e.HasIndex(x => x.BranchId);
            // Çevrimdışı POS idempotency: aynı istemci-satış-kimliği en fazla bir kez (NULL'lar hariç → çevrimiçi satışlar serbest).
            e.HasIndex(x => x.ClientSaleId).IsUnique().HasFilter("\"ClientSaleId\" IS NOT NULL");
            e.HasOne(x => x.Contact).WithMany()
                .HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<PurchaseOrder>(e =>
        {
            e.ToTable("purchase_orders");
            e.HasQueryFilter(x => _branchFilter == null || x.BranchId == _branchFilter); // şube izolasyonu
            e.Property(x => x.Number).HasMaxLength(40).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.Property(x => x.CreatedByName).HasMaxLength(256);
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.BranchId);
            e.HasOne(x => x.Contact).WithMany()
                .HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PurchaseOrderLine>(e =>
        {
            e.ToTable("purchase_order_lines");
            e.Property(x => x.ProductName).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.PurchaseOrderId);
            e.HasIndex(x => x.ProductId);
            e.HasOne(x => x.PurchaseOrder).WithMany(p => p.Lines)
                .HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Product>().WithMany()
                .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Quote>(e =>
        {
            e.ToTable("quotes");
            e.HasQueryFilter(x => _branchFilter == null || x.BranchId == _branchFilter); // şube izolasyonu
            e.Property(x => x.Number).HasMaxLength(40).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.CustomerName).HasMaxLength(160);
            e.Property(x => x.Note).HasMaxLength(2000);
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.Date);
            e.HasIndex(x => x.BranchId);
            e.HasIndex(x => x.Status);
            // Çift dönüşüme karşı DB güvencesi: bir fatura en fazla bir teklife bağlanır.
            e.HasIndex(x => x.InvoiceId).IsUnique().HasFilter("\"InvoiceId\" IS NOT NULL");
            e.HasOne(x => x.Contact).WithMany()
                .HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<QuoteLine>(e =>
        {
            e.ToTable("quote_lines");
            e.Property(x => x.ProductName).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.QuoteId);
            e.HasOne(x => x.Quote).WithMany(q => q.Lines)
                .HasForeignKey(x => x.QuoteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Product).WithMany()
                .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<InvoiceLine>(e =>
        {
            e.ToTable("invoice_lines");
            e.Property(x => x.ProductName).HasMaxLength(200).IsRequired();
            e.HasOne(x => x.Invoice).WithMany(i => i.Lines)
                .HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Product).WithMany()
                .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.Property(x => x.Direction).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => x.ContactId);
            e.HasIndex(x => x.Date);
        });

        b.Entity<TenantSettings>(e =>
        {
            e.ToTable("settings");
            e.Property(x => x.CompanyName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(8);
        });

        b.Entity<ServiceArea>(e =>
        {
            e.ToTable("service_areas");
            e.HasQueryFilter(x => _branchFilter == null || x.BranchId == _branchFilter); // şube izolasyonu
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
        });

        b.Entity<DiningTable>(e =>
        {
            e.ToTable("dining_tables");
            e.HasQueryFilter(x => _branchFilter == null || x.BranchId == _branchFilter); // şube izolasyonu
            e.Property(x => x.Name).HasMaxLength(80).IsRequired();
            e.HasOne(x => x.Area).WithMany(a => a.Tables)
                .HasForeignKey(x => x.AreaId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Order>(e =>
        {
            e.ToTable("orders");
            // ŞUBE İZOLASYONU (global filtre): şubeye kilitli kullanıcı yalnız kendi şubesinin
            // kayıtlarını GÖRÜR — GetById dahil TÜM LINQ sorgularına otomatik uygulanır; böylece
            // her by-id yükleme noktasını tek tek süzme ihtiyacı kalkar (IDOR/sızıntı tek yerde kapanır).
            e.HasQueryFilter(x => _branchFilter == null || x.BranchId == _branchFilter);
            // İyimser eşzamanlılık: aynı açık adisyona iki paralel kapatma (çift-tık / iki cihaz)
            // → biri DbUpdateConcurrencyException ile geri alınır (409), çift fatura/ödeme/stok
            // oluşmaz. xmin sistem sütunu olduğundan migration gerekmez.
            e.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.WorkStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Label).HasMaxLength(120);
            e.Property(x => x.AssetInfo).HasMaxLength(120);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.TableId);
            e.HasIndex(x => x.BranchId);
            e.HasOne(x => x.Table).WithMany()
                .HasForeignKey(x => x.TableId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Appointment>(e =>
        {
            e.ToTable("appointments");
            e.HasQueryFilter(x => _branchFilter == null || x.BranchId == _branchFilter); // şube izolasyonu
            e.Property(x => x.CustomerName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Phone).HasMaxLength(40);
            e.Property(x => x.ServiceName).HasMaxLength(200);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.ProductIds).HasMaxLength(500);
            e.HasIndex(x => x.StartsAt);
            e.HasIndex(x => x.ContactId);
            // Invoice→Contact ile aynı davranış: cari kaybolursa randevu silinmez, bağ kopar.
            e.HasOne(x => x.Contact).WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<OrderLine>(e =>
        {
            e.ToTable("order_lines");
            e.Property(x => x.ProductName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Note).HasMaxLength(300);
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => x.ProductId);
            e.HasOne(x => x.Order).WithMany(o => o.Lines)
                .HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Product).WithMany()
                .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<MarketplaceConnection>(e =>
        {
            e.ToTable("marketplace_connections");
            e.Property(x => x.Channel).HasMaxLength(40).IsRequired();
            e.Property(x => x.SupplierId).HasMaxLength(80).IsRequired();
            e.Property(x => x.ApiKeyEnc).IsRequired();
            e.Property(x => x.ApiSecretEnc).IsRequired();
            e.Property(x => x.LastStatus).HasMaxLength(20);
            e.Property(x => x.LastMessage).HasMaxLength(1000);
            e.HasIndex(x => x.Channel).IsUnique(); // tenant başına kanal tekil
        });

        b.Entity<MarketplaceListing>(e =>
        {
            e.ToTable("marketplace_listings");
            e.Property(x => x.MarketplaceBarcode).HasMaxLength(80).IsRequired();
            e.Property(x => x.ListingStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.AttributesJson).HasColumnType("jsonb");
            e.Property(x => x.BatchRequestId).HasMaxLength(80);
            e.Property(x => x.ListingError).HasMaxLength(1000);
            e.HasIndex(x => new { x.ConnectionId, x.MarketplaceBarcode }).IsUnique();
            e.HasIndex(x => x.ProductId);
            e.HasOne(x => x.Connection).WithMany()
                .HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Product).WithMany()
                .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MarketplaceOrder>(e =>
        {
            e.ToTable("marketplace_orders");
            e.Property(x => x.Channel).HasMaxLength(40).IsRequired();
            e.Property(x => x.MarketplaceOrderNumber).HasMaxLength(80).IsRequired();
            e.Property(x => x.BuyerName).HasMaxLength(200);
            e.Property(x => x.MarketplaceStatus).HasMaxLength(40);
            e.Property(x => x.SyncStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.SyncError).HasMaxLength(1000);
            // Dedupe bağlantı (kanal) başına: farklı pazaryerleri aynı sipariş no'yu kullanabilir.
            e.HasIndex(x => new { x.ConnectionId, x.MarketplaceOrderNumber }).IsUnique();
            e.HasIndex(x => x.OrderDate);
            e.HasOne(x => x.Connection).WithMany()
                .HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        });

        // AI Asistan öğrenme tabloları — tenant geneli (şube filtresi yok).
        b.Entity<AssistantTraining>(e =>
        {
            e.ToTable("assistant_training");
            e.Property(x => x.Intent).HasMaxLength(40).IsRequired();
            e.Property(x => x.Text).HasMaxLength(500).IsRequired();
            e.Property(x => x.Source).HasMaxLength(20).IsRequired();
            e.HasIndex(x => x.Intent);
        });
        b.Entity<AssistantUnresolved>(e =>
        {
            e.ToTable("assistant_unresolved");
            e.Property(x => x.Question).HasMaxLength(500).IsRequired();
            e.Property(x => x.QuestionKey).HasMaxLength(500).IsRequired();
            e.Property(x => x.ResolvedIntent).HasMaxLength(40);
            e.Property(x => x.AskedByEmail).HasMaxLength(256);
            e.HasIndex(x => x.IsResolved);
            // Çözülmemişler arasında normalize anahtar tekildir → dedup garantisi (eşzamanlı çift-ekleme engellenir).
            e.HasIndex(x => x.QuestionKey).IsUnique().HasFilter("\"IsResolved\" = false");
        });

        b.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            // Şube izolasyonu: kısıtlı kullanıcı yalnız kendi şubesinin + tenant-geneli (null) bildirimlerini görür.
            e.HasQueryFilter(x => _branchFilter == null || x.BranchId == null || x.BranchId == _branchFilter);
            e.Property(x => x.Type).HasMaxLength(40).IsRequired();
            e.Property(x => x.Severity).HasMaxLength(20).IsRequired();
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Message).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Link).HasMaxLength(300);
            e.Property(x => x.Icon).HasMaxLength(40);
            e.Property(x => x.DedupKey).HasMaxLength(200);
            e.HasIndex(x => x.IsRead);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.BranchId);
            // Tekilleştirme: aynı okunmamış olay için tek satır (eşzamanlı taramada da çift açılmaz).
            e.HasIndex(x => x.DedupKey).IsUnique().HasFilter("\"DedupKey\" IS NOT NULL AND \"IsRead\" = false");
        });

        b.Entity<Cheque>(e =>
        {
            e.ToTable("cheques");
            e.HasQueryFilter(x => _branchFilter == null || x.BranchId == null || x.BranchId == _branchFilter);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Direction).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ContactName).HasMaxLength(200);
            e.Property(x => x.Bank).HasMaxLength(120);
            e.Property(x => x.SerialNo).HasMaxLength(80);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => x.DueDate);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.ContactId);
        });

        b.Entity<RecipeComponent>(e =>
        {
            e.ToTable("recipe_components");
            e.HasIndex(x => x.ProductId);
            // Bir bitmiş üründe aynı bileşen tek satır.
            e.HasIndex(x => new { x.ProductId, x.ComponentProductId }).IsUnique();
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ComponentProductId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProductSupplier>(e =>
        {
            e.ToTable("product_suppliers");
            e.Property(x => x.SupplierSku).HasMaxLength(80);
            e.HasIndex(x => x.ProductId);
            // Bir üründe aynı tedarikçi tek satır.
            e.HasIndex(x => new { x.ProductId, x.ContactId }).IsUnique();
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Attachment>(e =>
        {
            e.ToTable("attachments");
            e.Property(x => x.OwnerType).HasMaxLength(20).IsRequired();
            e.Property(x => x.Url).HasMaxLength(500).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(120);
            e.Property(x => x.Note).HasMaxLength(500);
            // Polimorfik sahiplik: bir kaydın eklerini tek indeksle çeker (gerçek FK yok — çapraz-modül).
            e.HasIndex(x => new { x.OwnerType, x.OwnerId });
        });

        b.Entity<WarrantyRecord>(e =>
        {
            e.ToTable("warranty_records");
            e.HasQueryFilter(x => _branchFilter == null || x.BranchId == null || x.BranchId == _branchFilter); // şube izolasyonu
            e.Property(x => x.CustomerName).HasMaxLength(200).IsRequired();
            e.Property(x => x.ProductName).HasMaxLength(200).IsRequired();
            e.Property(x => x.SerialNo).HasMaxLength(80);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => x.PurchaseDate);
            e.HasIndex(x => x.ContactId);
            e.HasIndex(x => x.BranchId);
            e.HasOne(x => x.Contact).WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Referral>(e =>
        {
            e.ToTable("referrals");
            e.Property(x => x.Code).HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => x.ReferrerContactId);
        });

        b.Entity<GiftCard>(e =>
        {
            e.ToTable("gift_cards");
            // İyimser eşzamanlılık: Balance PARA taşır ve redeem'de okuma-değiştirme-yazma yapılır. Aynı koda
            // paralel iki harcama (çift-tık / iki kasa) ikisi de Balance=100 okur, ikisi de "Active" + tutar<=bakiye
            // kontrolünü geçer → 100 TL'lik çek iki kez harcanır = gerçek para kaybı. Diğer para yollarının aksine
            // redeem başka HİÇBİR jetonlu kaydı (kasa/cari/ürün) yazmaz, yani onu serileştiren bir şey yok —
            // kendi jetonu şart. Çakışan işlem tümüyle geri alınır → 409 "tekrar deneyin".
            // xmin sistem sütunu olduğundan migration gerekmez.
            e.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
            e.Property(x => x.Code).HasMaxLength(40).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => x.ContactId);
        });

        b.Entity<AutomationRule>(e =>
        {
            e.ToTable("automation_rules");
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.TriggerType).HasMaxLength(40).IsRequired();
            e.Property(x => x.ActionType).HasMaxLength(20).IsRequired();
            e.Property(x => x.ConditionJson).HasColumnType("jsonb");
            e.Property(x => x.ActionConfigJson).HasColumnType("jsonb");
            e.HasIndex(x => x.TriggerType);
            e.HasIndex(x => x.IsActive);
        });

        b.Entity<CommissionRule>(e =>
        {
            e.ToTable("commission_rules");
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => x.StaffUserId);
            e.HasIndex(x => x.IsActive);
        });

        b.Entity<Campaign>(e =>
        {
            e.ToTable("campaigns");
            e.Property(x => x.Name).HasMaxLength(160).IsRequired();
            e.Property(x => x.Type).HasMaxLength(30).IsRequired();
            e.Property(x => x.DaysMask).HasMaxLength(30);
            e.HasIndex(x => x.IsActive);
            e.HasIndex(x => x.Type);
        });

        b.Entity<ProductOption>(e =>
        {
            e.ToTable("product_options");
            e.Property(x => x.GroupName).HasMaxLength(80).IsRequired();
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.HasIndex(x => x.ProductId);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PushSubscription>(e =>
        {
            e.ToTable("push_subscriptions");
            e.Property(x => x.Endpoint).HasMaxLength(600).IsRequired();
            e.Property(x => x.P256dh).HasMaxLength(200).IsRequired();
            e.Property(x => x.Auth).HasMaxLength(200).IsRequired();
            e.Property(x => x.UserAgent).HasMaxLength(300);
            e.HasIndex(x => x.Endpoint).IsUnique(); // aynı cihaz tek satır (upsert anahtarı)
            e.HasIndex(x => x.UserId);
            // Şube filtresi YOK: abonelik kullanıcıya bağlı, tenant geneli push hedefi.
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
            if (entry.State == EntityState.Modified) entry.Entity.UpdatedAt = DateTime.UtcNow;
        return base.SaveChangesAsync(cancellationToken);
    }

    public async Task<long> NextDocumentNumberAsync(string key, long seedIfNew, CancellationToken ct = default)
    {
        // Sayaç tablosunu tenant şemasında (yoksa) oluştur — mevcut tenant'lar için kendini onarır.
        await Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS document_counters (key varchar(40) PRIMARY KEY, value bigint NOT NULL);", ct);

        // Atomik artış: ON CONFLICT sayesinde eşzamanlı çağrılar bile çift numara üretmez.
        var keyParam = new NpgsqlParameter("key", key);
        var seedParam = new NpgsqlParameter("seed", seedIfNew);
        var rows = await Database.SqlQueryRaw<long>(
            """
            INSERT INTO document_counters (key, value) VALUES (@key, @seed)
            ON CONFLICT (key) DO UPDATE SET value = document_counters.value + 1
            RETURNING value AS "Value";
            """,
            keyParam, seedParam).ToListAsync(ct);

        return rows[0];
    }
}
