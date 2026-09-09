using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Modules.Auth;
using CloudPosGrid.Application.Modules.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudPosGrid.Api.Controllers;

[ApiController]
[Route("api/settings")]
[Authorize]
public class SettingsController : ControllerBase
{
    private readonly ISettingsService _service;
    private readonly IAuthService _auth;
    private readonly ICurrentUser _currentUser;

    public SettingsController(ISettingsService service, IAuthService auth, ICurrentUser currentUser)
    {
        _service = service;
        _auth = auth;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<SettingsDto>> Get(CancellationToken ct)
        => Ok(await _service.GetAsync(ct));

    [HttpPut]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<SettingsDto>> Update(UpdateSettingsRequest req, CancellationToken ct)
        => Ok(await _service.UpdateAsync(req, ct));

    /// <summary>İşletme tipini (sektör) değiştirir; güncel kullanıcı/işletme bilgisini döner.</summary>
    [HttpPut("business-type")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<ActionResult<UserDto>> ChangeBusinessType(ChangeBusinessTypeRequest req, CancellationToken ct)
        => Ok(await _auth.ChangeBusinessTypeAsync(_currentUser.UserId!.Value, req.BusinessType, ct));
}
