using System.Text.RegularExpressions;
using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using CloudPosGrid.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Infrastructure.MultiTenancy;

/// <summary>
/// Yeni işletme için PostgreSQL şeması oluşturur, tabloları (EF model'inden üretilen DDL ile)
/// o şemada kurar ve varsayılan kayıtları seed'ler. Tüm işlemler search_path = yeni şema iken çalışır.
/// </summary>
public sealed partial class TenantProvisioner : ITenantProvisioner
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly TenantSchemaMigrator _migrator;

    public TenantProvisioner(AppDbContext db, ITenantContext tenant, TenantSchemaMigrator migrator)
    {
        _db = db;
        _tenant = tenant;
        _migrator = migrator;
    }

    /// <summary>
    /// Yarım kalmış kurulumun şemasını düşürür. Kurulum akışının catch bloğundan çağrılır;
    /// bu olmadan master kayıtları geri alınsa bile PostgreSQL şeması yetim kalıyordu.
    /// </summary>
    public async Task DropSchemaAsync(string schemaName, CancellationToken ct = default)
    {
        if (!SchemaNameRegex().IsMatch(schemaName)) return; // asla tahmin edilmiş adla DROP çalıştırma
#pragma warning disable EF1002 // schemaName yukarıda regex ile doğrulandı
        await _db.Database.ExecuteSqlRawAsync($"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE;", ct);
#pragma warning restore EF1002
    }

    public async Task ProvisionAsync(Guid tenantId, string schemaName, string companyName, BusinessType businessType, bool demoData = false, CancellationToken ct = default)
    {
        if (!SchemaNameRegex().IsMatch(schemaName))
            throw new InvalidOperationException($"Geçersiz şema adı: {schemaName}");

        // Bağlamı yeni şemaya yönlendir; interceptor bundan sonra search_path = schemaName ayarlar.
        _tenant.SetTenant(tenantId, schemaName);

        // 1) Şemayı oluştur. (DDL parametrelenemez; schemaName yukarıda regex ile doğrulandı.)
#pragma warning disable EF1002 // schemaName güvenli (^tenant_[a-z0-9]+$)
        await _db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA IF NOT EXISTS \"{schemaName}\";", ct);
#pragma warning restore EF1002

        // 2) Tabloları bu şemada kur (EF model'inden üretilen şema-bağımsız DDL).
        var createScript = _db.Database.GenerateCreateScript();
        await _db.Database.ExecuteSqlRawAsync(createScript, ct);

        // 2b) Trigram arama indeksleri (yeni tenant). Mevcut tenant'lar aynı SQL'i 0008 göçüyle alır.
#pragma warning disable EF1002 // sabit, güvenli DDL
        await _db.Database.ExecuteSqlRawAsync(Persistence.Migrations.Tenant.TenantMigrations.TrgmSql, ct);
#pragma warning restore EF1002

        // 3) Varsayılan kayıtlar (search_path = schemaName olduğu için doğru şemaya yazılır).
        var branch = new Branch { Name = "Merkez", IsDefault = true, IsActive = true };
        _db.Branches.Add(branch);
        var kasa = new CashAccount { Name = "Nakit Kasa", Type = CashAccountType.Cash, BranchId = branch.Id };
        _db.CashAccounts.Add(kasa);
        _db.Settings.Add(new TenantSettings { CompanyName = companyName, Currency = "TRY", DefaultVatRate = 20m });
        if (demoData) SeedDemo(branch, kasa);
        else SeedSector(businessType);
        await _db.SaveChangesAsync(ct);

        // Şema en güncel modelden kuruldu -> tüm tenant göçlerini "uygulanmış" işaretle.
        await _migrator.BaselineAsync(ct);
    }

    /// <summary>Sektöre göre başlangıç kategorileri ve (varsa) hizmet kalemleri.</summary>
    private void SeedSector(BusinessType type)
    {
        switch (type)
        {
            case BusinessType.Hospitality:
                _db.Categories.AddRange(
                    new Category { Name = "Sıcak İçecekler" },
                    new Category { Name = "Soğuk İçecekler" },
                    new Category { Name = "Yiyecekler" });
                break;

            case BusinessType.Service:
            {
                var hizmet = new Category { Name = "Hizmetler" };
                _db.Categories.AddRange(hizmet, new Category { Name = "Yedek Parça" });
                _db.Products.Add(NewService("İşçilik", "SRV-ISCILIK", hizmet));
                break;
            }

            case BusinessType.Beauty:
            {
                var hizmet = new Category { Name = "Hizmetler" };
                _db.Categories.Add(hizmet);
                _db.Products.Add(NewService("Saç Kesimi", "SRV-KESIM", hizmet));
                _db.Products.Add(NewService("Fön", "SRV-FON", hizmet));
                break;
            }

            default: // Retail, General
                _db.Categories.Add(new Category { Name = "Genel" });
                break;
        }
    }

    /// <summary>
    /// Demo işletme için zengin örnek veri: ürünler, masalar, cariler ve 14 günlük satış geçmişi.
    /// Sabit tohumlu Random ile her demo aynı ve tutarlı görünür.
    /// </summary>
    private void SeedDemo(Branch branch, CashAccount kasa)
    {
        var banka = new CashAccount { Name = "Banka Hesabı", Type = CashAccountType.Bank, BranchId = branch.Id };
        _db.CashAccounts.Add(banka);

        var sicak = new Category { Name = "Sıcak İçecekler" };
        var soguk = new Category { Name = "Soğuk İçecekler" };
        var yiyecek = new Category { Name = "Yiyecekler" };
        var tatli = new Category { Name = "Tatlılar" };
        _db.Categories.AddRange(sicak, soguk, yiyecek, tatli);

        static Product P(string sku, string name, Category cat, decimal sale, decimal purchase, decimal stock, decimal min, string? barcode = null) => new()
        {
            Sku = sku, Name = name, Category = cat, Unit = "adet",
            SalePrice = sale, PurchasePrice = purchase, VatRate = 10m,
            CurrentStock = stock, MinStock = min, Barcode = barcode,
            IsActive = true, IsVisibleOnMenu = true,
        };

        var urunler = new[]
        {
            P("KHV-001", "Türk Kahvesi", sicak, 45, 12, 80, 20),
            P("KHV-002", "Latte", sicak, 70, 22, 60, 15),
            P("KHV-003", "Filtre Kahve", sicak, 60, 18, 55, 15),
            P("CAY-001", "Çay", sicak, 20, 4, 200, 50),
            P("SGK-001", "Limonata", soguk, 50, 15, 40, 10),
            P("SGK-002", "Ayran", soguk, 25, 8, 8, 12),          // kritik stok örneği
            P("SGK-003", "Su", soguk, 10, 3, 150, 40, "8690000000019"),
            P("YMK-001", "Tost", yiyecek, 85, 30, 40, 10),
            P("YMK-002", "Sandviç", yiyecek, 120, 45, 25, 8),
            P("TTL-001", "Cheesecake", tatli, 110, 40, 12, 5),
            P("TTL-002", "Pasta Dilimi", tatli, 90, 35, 4, 6),   // kritik stok örneği
            P("TTL-003", "Kurabiye", tatli, 35, 10, 60, 15),
        };
        _db.Products.AddRange(urunler);

        // Çok-şube TAM stok: demo ürünlerinin stoğu varsayılan (Merkez) şubeye yazılmalı → satış oversell'e takılmasın.
        foreach (var p in urunler)
            _db.ProductBranchStocks.Add(new ProductBranchStock { Product = p, BranchId = branch.Id, Quantity = p.CurrentStock });

        var salon = new ServiceArea { Name = "Salon", SortOrder = 0, BranchId = branch.Id };
        var bahce = new ServiceArea { Name = "Bahçe", SortOrder = 1, BranchId = branch.Id };
        _db.ServiceAreas.AddRange(salon, bahce);
        var masalar = new List<DiningTable>();
        for (var i = 1; i <= 6; i++) masalar.Add(new DiningTable { Name = $"Salon {i}", Area = salon, SortOrder = i, BranchId = branch.Id });
        for (var i = 1; i <= 4; i++) masalar.Add(new DiningTable { Name = $"Bahçe {i}", Area = bahce, SortOrder = i, BranchId = branch.Id });
        _db.DiningTables.AddRange(masalar);

        _db.Contacts.AddRange(
            new Contact { Name = "Ayşe Yılmaz", Type = ContactType.Customer, Phone = "0532 111 22 33", DiscountRate = 10 },
            new Contact { Name = "Mehmet Demir", Type = ContactType.Customer, Phone = "0533 444 55 66" },
            new Contact { Name = "Kahve Tedarik Ltd.", Type = ContactType.Supplier, Phone = "0212 555 00 11" });

        // ---- 14 günlük satış geçmişi (panel grafikleri ve raporlar dolu görünsün) ----
        var rnd = new Random(2025);
        var now = DateTime.UtcNow;
        var no = 0;
        for (var day = 13; day >= 0; day--)
        {
            var date = now.Date.AddDays(-day).AddHours(9);
            var salesCount = rnd.Next(2, 5); // günde 2-4 satış
            for (var s = 0; s < salesCount; s++)
            {
                var inv = new Invoice
                {
                    Type = InvoiceType.Sales,
                    Number = $"SLS-{date:yyyy}-{++no:D5}",
                    Date = date.AddMinutes(rnd.Next(0, 600)),
                    Status = InvoiceStatus.Paid,
                    BranchId = branch.Id,
                };
                decimal sub = 0, vat = 0;
                var lineCount = rnd.Next(1, 4);
                for (var l = 0; l < lineCount; l++)
                {
                    var p = urunler[rnd.Next(urunler.Length)];
                    var qty = rnd.Next(1, 4);
                    var lineTotal = Math.Round(qty * p.SalePrice, 2);
                    var vatAmount = Math.Round(lineTotal * p.VatRate / 100m, 2);
                    inv.Lines.Add(new InvoiceLine
                    {
                        Product = p, ProductName = p.Name,
                        Quantity = qty, UnitPrice = p.SalePrice, VatRate = p.VatRate,
                        LineTotal = lineTotal, VatAmount = vatAmount,
                    });
                    sub += lineTotal; vat += vatAmount;
                }
                inv.Subtotal = Math.Round(sub, 2);
                inv.VatTotal = Math.Round(vat, 2);
                inv.GrandTotal = inv.Subtotal + inv.VatTotal;
                inv.PaidAmount = inv.GrandTotal;
                _db.Invoices.Add(inv);

                var method = rnd.Next(2) == 0 ? PaymentMethod.Cash : PaymentMethod.Card;
                var hesap = method == PaymentMethod.Cash ? kasa : banka;
                hesap.Balance += inv.GrandTotal; // kasa bakiyesi denormalize tutulur
                _db.Payments.Add(new Payment
                {
                    InvoiceId = inv.Id, CashAccountId = hesap.Id, Amount = inv.GrandTotal,
                    Direction = PaymentDirection.In, Method = method, Date = inv.Date,
                });
                _db.FinanceTransactions.Add(new FinanceTransaction
                {
                    CashAccountId = hesap.Id, Type = FinanceType.Income, Category = "Satış",
                    Amount = inv.GrandTotal, Description = $"Satış {inv.Number}",
                    PaymentMethod = method, RefId = inv.Id, BranchId = branch.Id, Date = inv.Date,
                });
            }
        }

        // Birkaç gider — net kâr gerçekçi görünsün.
        _db.FinanceTransactions.AddRange(
            new FinanceTransaction { CashAccountId = banka.Id, Type = FinanceType.Expense, Category = "Kira", Amount = 1500, Description = "Haftalık kira", PaymentMethod = PaymentMethod.Transfer, BranchId = branch.Id, Date = now.AddDays(-6) },
            new FinanceTransaction { CashAccountId = kasa.Id, Type = FinanceType.Expense, Category = "Malzeme", Amount = 850, Description = "Kahve çekirdeği alımı", PaymentMethod = PaymentMethod.Cash, BranchId = branch.Id, Date = now.AddDays(-3) });
        banka.Balance -= 1500;
        kasa.Balance -= 850;

        // İki masada açık adisyon — Masalar ekranı canlı görünsün.
        Order Adisyon(DiningTable masa, int dakikaOnce, params (Product p, int qty)[] kalemler)
        {
            var o = new Order
            {
                Type = OrderType.DineIn, Status = OrderStatus.Open, Source = OrderSource.Pos,
                Table = masa, BranchId = branch.Id, OpenedAt = now.AddMinutes(-dakikaOnce),
            };
            decimal sub = 0, vat = 0;
            foreach (var (p, qty) in kalemler)
            {
                var lt = Math.Round(qty * p.SalePrice, 2);
                o.Lines.Add(new OrderLine { ProductId = p.Id, ProductName = p.Name, Quantity = qty, UnitPrice = p.SalePrice, VatRate = p.VatRate, LineTotal = lt });
                sub += lt; vat += Math.Round(lt * p.VatRate / 100m, 2);
            }
            o.Subtotal = Math.Round(sub, 2);
            o.VatTotal = Math.Round(vat, 2);
            o.GrandTotal = o.Subtotal + o.VatTotal;
            return o;
        }
        _db.Orders.Add(Adisyon(masalar[1], 45, (urunler[0], 2), (urunler[9], 1)));
        _db.Orders.Add(Adisyon(masalar[7], 20, (urunler[3], 3), (urunler[7], 1)));
    }

    private static Product NewService(string name, string sku, Category category) => new()
    {
        Sku = sku,
        Name = name,
        Unit = "hizmet",
        IsService = true,
        VatRate = 20m,
        IsActive = true,
        Category = category,
    };

    [GeneratedRegex("^tenant_[a-z0-9]+$")]
    private static partial Regex SchemaNameRegex();
}
