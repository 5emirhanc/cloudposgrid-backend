using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Admin;
using CloudPosGrid.Application.Modules.Dealers;
using CloudPosGrid.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>
/// Platform (SaaS sahibi) yönetim paneli. Yalnızca süper-admin (config allowlist) erişir.
/// Tüm işletmeleri, paketleri, denemeleri yönetir; havale onaylarını yürütür; bayileri (#25) tanımlar.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = "PlatformAdmin")]
public class AdminController : ControllerBase
{
    private readonly IAdminService _service;
    private readonly IDealerService _dealers;
    private readonly Common.TenantPurger _purger;
    private readonly Infrastructure.Persistence.MasterDbContext _master;
    private readonly Application.Abstractions.IPlatformInfo _platform;

    public AdminController(
        IAdminService service, IDealerService dealers, Common.TenantPurger purger,
        Infrastructure.Persistence.MasterDbContext master, Application.Abstractions.IPlatformInfo platform)
    {
        _service = service;
        _dealers = dealers;
        _purger = purger;
        _master = master;
        _platform = platform;
    }

    // ---- Bayi (#25) yönetimi — süper-admin bayileri tanımlar/aktifleştirir ----
    [HttpGet("dealers")]
    public async Task<ActionResult<IReadOnlyList<DealerDto>>> Dealers(CancellationToken ct)
        => Ok(await _dealers.ListDealersAsync(ct));

    [HttpPost("dealers")]
    public async Task<ActionResult<DealerDto>> CreateDealer(CreateDealerRequest req, CancellationToken ct)
        => Ok(await _dealers.CreateDealerAsync(req, ct));

    [HttpPost("dealers/{id:guid}/active")]
    public async Task<IActionResult> SetDealerActive(Guid id, SetDealerActiveRequest req, CancellationToken ct)
    {
        await _dealers.SetActiveAsync(id, req.IsActive, ct);
        return NoContent();
    }

    /// <summary>Bir bayinin tüm tablosu: para durumu, getirdiği müşteriler ve ödeme geçmişi.</summary>
    [HttpGet("dealers/{id:guid}")]
    public async Task<ActionResult<DealerDetailDto>> DealerDetail(Guid id, CancellationToken ct)
        => Ok(await _dealers.GetDealerDetailAsync(id, ct));

    /// <summary>Bayiye yapılan ödemeyi (hakediş mahsuplaşması) kaydeder.</summary>
    [HttpPost("dealers/{id:guid}/payouts")]
    public async Task<ActionResult<DealerPayoutDto>> CreateDealerPayout(Guid id, CreatePayoutRequest req, CancellationToken ct)
        => Ok(await _dealers.RecordPayoutAsync(id, req, ct));

    /// <summary>Bayinin şifresini sıfırlar (bayi kendi şifresini değiştiremiyor).</summary>
    [HttpPost("dealers/{id:guid}/password")]
    public async Task<IActionResult> ResetDealerPassword(Guid id, ResetDealerPasswordRequest req, CancellationToken ct)
    {
        await _dealers.ResetDealerPasswordAsync(id, req.NewPassword, ct);
        return NoContent();
    }

    /// <summary>
    /// Bayiyi siler. Getirdiği işletmeler silinmez, yalnız bayi atıfını kaybeder.
    /// Kapatılmamış hakediş varsa reddedilir; onay için bayinin adı birebir yazılmalıdır.
    /// </summary>
    [HttpDelete("dealers/{id:guid}")]
    public async Task<IActionResult> DeleteDealer(Guid id, DeleteDealerRequest req, CancellationToken ct)
    {
        await _dealers.DeleteDealerAsync(id, req.ConfirmName, ct);
        return NoContent();
    }

    [HttpGet("tenants")]
    public async Task<ActionResult<PagedResult<TenantAdminDto>>> Tenants(
        [FromQuery] string? filter, [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
        => Ok(await _service.GetTenantsAsync(filter, search, page, pageSize, ct));

    [HttpGet("stats")]
    public async Task<ActionResult<AdminStatsDto>> Stats(CancellationToken ct)
        => Ok(await _service.GetStatsAsync(ct));

    /// <summary>Değişmez denetim izi — para/erişim etkileyen admin ve hesap eylemleri (en yeni önce).</summary>
    [HttpGet("audit")]
    public async Task<ActionResult<IReadOnlyList<AuditLogDto>>> Audit([FromQuery] int limit = 100, CancellationToken ct = default)
        => Ok(await _service.GetAuditLogsAsync(limit, ct));

    [HttpPost("tenants/{id:guid}/activate")]
    public async Task<ActionResult<TenantAdminDto>> Activate(Guid id, ActivateSubscriptionRequest req, CancellationToken ct)
        => Ok(await _service.ActivateAsync(id, req, ct));

    [HttpPost("tenants/{id:guid}/extend")]
    public async Task<ActionResult<TenantAdminDto>> Extend(Guid id, ExtendRequest req, CancellationToken ct)
        => Ok(await _service.ExtendAsync(id, req.Days, ct));

    [HttpPost("tenants/{id:guid}/suspend")]
    public async Task<ActionResult<TenantAdminDto>> Suspend(Guid id, AdminNoteRequest req, CancellationToken ct)
        => Ok(await _service.SuspendAsync(id, req.Note, ct));

    /// <summary>
    /// İşletmeyi KALICI olarak siler: kiracı şeması DROP edilir, master kayıtları ve yüklenen
    /// görseller kaldırılır. GERİ DÖNÜŞÜ YOKTUR — "askıya al" (geçici kilit) ve "iptal et"
    /// (abonelik sonlandırma) bundan ayrıdır ve veriye dokunmaz.
    ///
    /// Kaza koruması: gövdedeki ConfirmName, işletmenin adıyla birebir eşleşmelidir. Silme izi
    /// AuditLog'a yazılır ve kiracı gittikten sonra da kalır (bu tabloda Tenant'a FK yoktur).
    /// </summary>
    [HttpDelete("tenants/{id:guid}")]
    public async Task<IActionResult> DeleteTenant(Guid id, DeleteTenantRequest req, CancellationToken ct)
    {
        var tenant = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(_master.Tenants, t => t.Id == id, ct)
            ?? throw NotFoundException.For("İşletme", id);

        // Adı elle yazdırmak, listeden yanlış satıra basmaya karşı tek gerçek koruma:
        // kimlik (id) doğru olsa bile yönetici SİLDİĞİ ŞEYİ okumak zorunda kalır.
        if (!string.Equals(req.ConfirmName?.Trim(), tenant.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new BusinessRuleException(
                $"Silmeyi onaylamak için işletmenin adını birebir yazın: \"{tenant.Name}\". Hiçbir şey silinmedi.");

        await _purger.PurgeAsync(tenant, _platform.AdminEmail, "TenantDeletedByAdmin", ct);
        return NoContent();
    }

    [HttpPost("tenants/{id:guid}/cancel")]
    public async Task<ActionResult<TenantAdminDto>> Cancel(Guid id, AdminNoteRequest req, CancellationToken ct)
        => Ok(await _service.CancelAsync(id, req.Note, ct));

    [HttpGet("requests")]
    public async Task<ActionResult<List<SubscriptionRequestDto>>> Requests([FromQuery] string? status, CancellationToken ct)
    {
        SubscriptionRequestStatus? s = Enum.TryParse<SubscriptionRequestStatus>(status, true, out var v) ? v : null;
        return Ok(await _service.GetRequestsAsync(s, ct));
    }

    [HttpPost("requests/{id:guid}/approve")]
    public async Task<ActionResult<TenantAdminDto>> Approve(Guid id, AdminNoteRequest req, CancellationToken ct)
        => Ok(await _service.ApproveRequestAsync(id, req.Note, ct));

    [HttpPost("requests/{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, AdminNoteRequest req, CancellationToken ct)
    {
        await _service.RejectRequestAsync(id, req.Note, ct);
        return NoContent();
    }
}
