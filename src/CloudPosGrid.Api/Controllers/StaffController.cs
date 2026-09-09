using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Application.Modules.Staff;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

/// <summary>Personel (kullanıcı) yönetimi — yalnızca işletme sahibi ve yöneticiler erişebilir.
/// Personel EKLEME/PIN atama Kurumsal pakete özeldir; okuma/düzenleme/silme (mevcut personeli
/// yönetmek — ör. plan düşünce temizlemek) açık kalır.</summary>
[ApiController]
[Route("api/staff")]
[Authorize(Roles = "Owner,Admin")]
public class StaffController : ControllerBase
{
    private readonly IStaffService _service;
    private readonly ICurrentUser _currentUser;
    private readonly IPlanEntitlementProvider _entitlements;

    public StaffController(IStaffService service, ICurrentUser currentUser, IPlanEntitlementProvider entitlements)
    {
        _service = service;
        _currentUser = currentUser;
        _entitlements = entitlements;
    }

    private Guid TenantId => _currentUser.TenantId!.Value;

    private async Task EnsureStaffPlanAsync(CancellationToken ct)
    {
        var e = await _entitlements.GetAsync(ct);
        if (!e.StaffManagement)
            throw new PlanUpgradeException("Personel ve rol yönetimi Kurumsal pakete özeldir. Kullanmak için paketinizi yükseltin.");
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<StaffDto>>> List(CancellationToken ct)
        => Ok(await _service.ListAsync(TenantId, ct));

    [HttpPost]
    public async Task<ActionResult<StaffDto>> Create(CreateStaffRequest req, CancellationToken ct)
    {
        await EnsureStaffPlanAsync(ct);
        // Yetki kontrolü AKTİF şubeye değil, işlemi yapanın TÜM izinli şubelerine göre yapılır.
        return Ok(await _service.CreateAsync(TenantId, _currentUser.AllowedBranchIds, req, ct));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<StaffDto>> Update(Guid id, UpdateStaffRequest req, CancellationToken ct)
        => Ok(await _service.UpdateAsync(TenantId, _currentUser.UserId!.Value, _currentUser.AllowedBranchIds, id, req, ct));

    [HttpPut("{id:guid}/pin")]
    public async Task<IActionResult> SetPin(Guid id, SetPinRequest req, CancellationToken ct)
    {
        await EnsureStaffPlanAsync(ct);
        // Şubeye kilitli yönetici yalnız kendi şubelerinin personeline dokunabilir.
        await _service.SetPinAsync(TenantId, _currentUser.AllowedBranchIds, id, req.Pin, ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _service.DeleteAsync(TenantId, _currentUser.UserId!.Value, _currentUser.AllowedBranchIds, id, ct);
        return NoContent();
    }
}
