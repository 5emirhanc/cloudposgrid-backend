using System.Text.RegularExpressions;
using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using CloudPosGrid.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Api.Common;

/// <summary>
/// Hesap sahibinin KVKK haklarını karşılar: verilerini dışa aktarma (erişim/taşınabilirlik) ve hesabını
/// silme (unutulma hakkı). MaintenanceService ile aynı desen (Api katmanı + concrete MasterDbContext) —
/// şema DROP master bağlantısından yapılır, kurban şemadan değil.
/// </summary>
public sealed class AccountService
{
    private readonly MasterDbContext _master;
    private readonly IApplicationDbContext _app;
    private readonly ICurrentUser _currentUser;
    private readonly IPasswordHasher _hasher;
    private readonly TenantPurger _purger;

    public AccountService(MasterDbContext master, IApplicationDbContext app, ICurrentUser currentUser,
        IPasswordHasher hasher, TenantPurger purger)
    {
        _master = master;
        _app = app;
        _currentUser = currentUser;
        _hasher = hasher;
        _purger = purger;
    }

    /// <summary>Oturumdaki kullanıcının kişisel + işletme verilerini yapılandırılmış döndürür (KVKK erişim/taşınabilirlik).</summary>
    public async Task<AccountExportDto> ExportAsync(CancellationToken ct = default)
    {
        var userId = _currentUser.UserId ?? throw new BusinessRuleException("Oturum bulunamadı.");
        var user = await _master.Users.Include(u => u.Tenant).AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw NotFoundException.For("Kullanıcı", userId);
        var t = user.Tenant;

        var products = await _app.Products.AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(p => new ExportProductDto(p.Name, p.Sku, p.Barcode, p.SalePrice, p.CurrentStock, p.IsActive))
            .ToListAsync(ct);

        var contacts = await _app.Contacts.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new ExportContactDto(c.Name, c.Type.ToString(), c.Phone, c.Email, c.Address, c.Balance))
            .ToListAsync(ct);

        var invoices = await _app.Invoices.AsNoTracking()
            .OrderByDescending(i => i.Date)
            .Select(i => new ExportInvoiceDto(i.Number, i.Type.ToString(), i.Date, i.GrandTotal, i.Status.ToString()))
            .ToListAsync(ct);

        var account = new ExportAccountDto(user.Email, user.FullName, user.Role.ToString(),
            t.Name, t.Plan.ToString(), t.Status.ToString(), t.CreatedAt);

        return new AccountExportDto(DateTime.UtcNow, account, products, contacts, invoices);
    }

    /// <summary>Oturumdaki SAHİBİN işletmesini tümüyle siler: tenant şeması DROP + master kayıtları
    /// (kullanıcılar/token/talepler cascade). Şifre onayı ister; yalnız Owner çağırabilir. Değişmez audit izi bırakır.</summary>
    public async Task DeleteAsync(string password, CancellationToken ct = default)
    {
        var userId = _currentUser.UserId ?? throw new BusinessRuleException("Oturum bulunamadı.");
        var user = await _master.Users.Include(u => u.Tenant).FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw NotFoundException.For("Kullanıcı", userId);

        if (user.Role != UserRole.Owner)
            throw new BusinessRuleException("Hesabı yalnız işletme sahibi silebilir.");

        // #47: kimlik Account'ta. Onay şifresini GÜNCEL kimliğe (account.PasswordHash) karşı doğrula —
        // User.PasswordHash bayat kopyadır (şifre değişince güncellenmez) → eski/rotated şifre silmeyi yetkilendiremez.
        var account = await _master.Accounts.FirstOrDefaultAsync(a => a.Id == user.AccountId, ct)
            ?? throw NotFoundException.For("Hesap", user.AccountId);
        if (string.IsNullOrEmpty(password) || !_hasher.Verify(password, account.PasswordHash))
            throw new BusinessRuleException("Şifre hatalı. Hesap silinmedi.");

        // Silmenin tüm adımları (denetim izi → şema DROP → master kayıtları → yetim kimlik →
        // yüklenen görseller → cache) tek yerde: bkz. TenantPurger. Buradaki sorumluluk yalnız
        // YETKİLENDİRME (sahip mi, şifresi doğru mu); yıkım sırası ortak servise ait.
        await _purger.PurgeAsync(user.Tenant, user.Email, "AccountDeleted", ct);
    }
}

public record AccountExportDto(
    DateTime ExportedAt, ExportAccountDto Account,
    IReadOnlyList<ExportProductDto> Products, IReadOnlyList<ExportContactDto> Contacts, IReadOnlyList<ExportInvoiceDto> Invoices);
public record ExportAccountDto(string Email, string FullName, string Role, string BusinessName, string Plan, string Status, DateTime CreatedAt);
public record ExportProductDto(string Name, string? Sku, string? Barcode, decimal SalePrice, decimal CurrentStock, bool IsActive);
public record ExportContactDto(string Name, string Type, string? Phone, string? Email, string? Address, decimal Balance);
public record ExportInvoiceDto(string Number, string Type, DateTime Date, decimal GrandTotal, string Status);
