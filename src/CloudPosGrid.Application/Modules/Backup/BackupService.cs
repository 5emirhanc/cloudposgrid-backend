using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudPosGrid.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Backup;

/// <summary>
/// Self-servis yedek: kiracının (tenant) ana verilerini tek bir indirilebilir JSON belgesine toplar.
/// Salt-okunurdur; hiçbir veri değiştirmez. Amaç "verilerim kaybolur mu" endişesini gidermektir —
/// kullanıcı istediği an tüm verisinin bir kopyasını indirir.
/// </summary>
public interface IBackupService
{
    /// <summary>Ana verileri tek JSON nesnesine toplayıp UTF-8 byte dizisi olarak döndürür (indirilebilir yedek).</summary>
    Task<byte[]> ExportAsync(CancellationToken ct = default);
}

/// <summary>
/// <see cref="IBackupService"/> uygulaması. Şube filtreli tablolar (fatura, kasa, finans, stok hareketi)
/// <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters"/> ile okunur — yedek AKTİF şube seçimine
/// göre kırpılmamalıdır (kısmi yedek amacı boşa çıkarır).
/// ŞUBE İZOLASYONU: IgnoreQueryFilters kilidi kaldırdığı için, kullanıcı şubeye KİLİTLİYSE
/// (<see cref="ICurrentUser.AllowedBranchIds"/> dolu — ör. tek şubeye bağlı Admin) sorgular ELLE o şubelere
/// daraltılır. Aksi halde kilitli bir Admin, uygulamada göremediği diğer şubelerin faturalarını, satır
/// maliyetlerini (UnitCost), kasa bakiyelerini ve finans hareketlerini tek dosyada indirebilirdi.
/// Kısıtsız sahip (Owner) için davranış değişmez: yedek tüm şubeleri kapsar.
/// Büyük hareket tabloları en yeni <see cref="MaxRows"/> satırla sınırlanır; her sorgu <c>AsNoTracking</c>.
/// </summary>
public sealed class BackupService : IBackupService
{
    /// <summary>Büyük hareket tablolarında (stok/cari hareketi) yedeğe alınan en yeni satır sayısı.</summary>
    private const int MaxRows = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Türkçe karakterler okunur kalsın (ör. "İşletmem" \u.. olarak kaçışlanmasın).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;

    public BackupService(IApplicationDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<byte[]> ExportAsync(CancellationToken ct = default)
    {
        // İşletme ayarları (tek satır).
        var settings = await _db.Settings.AsNoTracking()
            .Select(s => new
            {
                s.CompanyName, s.TaxOffice, s.TaxNo, s.Address, s.Phone, s.Email,
                s.Currency, s.DefaultVatRate, s.LogoUrl,
            })
            .FirstOrDefaultAsync(ct);

        var categories = await _db.Categories.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name, c.ParentId, c.IsActive })
            .ToListAsync(ct);

        var products = await _db.Products.AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                p.Id, p.Sku, p.Barcode, p.Name, p.CategoryId, p.Unit,
                p.PurchasePrice, p.SalePrice, p.VatRate, p.CurrentStock, p.MinStock,
                p.IsActive, p.IsService, p.ExpiryDate, p.Description,
                p.ParentProductId, p.IsVariantParent, p.VariantValues,
            })
            .ToListAsync(ct);

        var contacts = await _db.Contacts.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new
            {
                c.Id, c.Type, c.Name, c.TaxOffice, c.TaxNo, c.Phone, c.Email, c.Address,
                c.Balance, c.DiscountRate, c.PointsBalance, c.IsActive, c.Notes, c.Tags, c.CreditLimit,
            })
            .ToListAsync(ct);

        // Kullanıcı şubeye kilitliyse (küme dolu) IgnoreQueryFilters ile açılan kapı ELLE daraltılır.
        var allowed = _currentUser.AllowedBranchIds.ToList();
        var restricted = allowed.Count > 0;

        // ---- Şube filtreli tablolar → IgnoreQueryFilters ile tüm şubeler (yedek eksiksiz olmalı) ----
        var cashAccounts = await _db.CashAccounts.AsNoTracking().IgnoreQueryFilters()
            .OrderBy(a => a.Name)
            .Where(a => !restricted || (a.BranchId != null && allowed.Contains(a.BranchId.Value)))
            .Select(a => new { a.Id, a.Name, a.Type, a.Balance, a.IsActive, a.BranchId })
            .ToListAsync(ct);

        var invoices = await _db.Invoices.AsNoTracking().IgnoreQueryFilters()
            .Where(i => !restricted || (i.BranchId != null && allowed.Contains(i.BranchId.Value)))
            .OrderByDescending(i => i.Date)
            .Select(i => new
            {
                i.Id, i.Type, i.Number, i.ContactId, i.Date, i.DueDate,
                i.Subtotal, i.VatTotal, i.GrandTotal, i.PaidAmount,
                i.Discount, i.ManualDiscount, i.Status, i.Note, i.BranchId, i.Channel,
                Lines = i.Lines
                    .Select(l => new
                    {
                        l.Id, l.ProductId, l.ProductName, l.Quantity, l.UnitPrice,
                        l.VatRate, l.LineTotal, l.VatAmount, l.UnitCost, l.RefundedQuantity,
                    })
                    .ToList(),
            })
            .ToListAsync(ct);

        var financeTransactions = await _db.FinanceTransactions.AsNoTracking().IgnoreQueryFilters()
            .Where(f => !restricted || (f.BranchId != null && allowed.Contains(f.BranchId.Value)))
            .OrderByDescending(f => f.Date)
            .Select(f => new
            {
                f.Id, f.CashAccountId, f.Type, f.Category, f.Amount, f.Description,
                f.PaymentMethod, f.ContactId, f.RefId, f.BranchId, f.Date,
            })
            .ToListAsync(ct);

        // Büyük tablolar: yalnız en yeni MaxRows kayıt (yedeği makul boyutta tutar).
        var stockMovements = await _db.StockMovements.AsNoTracking().IgnoreQueryFilters()
            .Where(m => !restricted || (m.BranchId != null && allowed.Contains(m.BranchId.Value)))
            .OrderByDescending(m => m.CreatedAt)
            .Take(MaxRows)
            .Select(m => new
            {
                m.Id, m.ProductId, m.Type, m.Quantity, m.UnitCost, m.Reference,
                m.RefId, m.Note, m.StockAfter, m.BranchId, m.CreatedAt,
            })
            .ToListAsync(ct);

        var accountTransactions = await _db.AccountTransactions.AsNoTracking()
            .OrderByDescending(t => t.Date)
            .Take(MaxRows)
            .Select(t => new
            {
                t.Id, t.ContactId, t.Direction, t.Amount, t.BalanceAfter,
                t.Description, t.DocRef, t.RefId, t.Date, t.DueDate,
            })
            .ToListAsync(ct);

        var snapshot = new
        {
            Meta = new
            {
                Format = "cloudposgrid-backup",
                Version = 1,
                ExportedAt = DateTime.UtcNow,
                Note = "CloudPosGrid self-servis yedek — salt-okunur veri kopyasıdır. " +
                       "Hareket tabloları en yeni 5000 satırla sınırlıdır.",
            },
            Settings = settings,
            Categories = categories,
            Products = products,
            Contacts = contacts,
            CashAccounts = cashAccounts,
            Invoices = invoices,
            FinanceTransactions = financeTransactions,
            StockMovements = stockMovements,
            AccountTransactions = accountTransactions,
        };

        return JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
    }
}
