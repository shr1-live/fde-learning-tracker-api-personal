using FdeTracker.Api.Filters;
using FdeTracker.Api.Models.Requests;
using FdeTracker.Api.Services;
using FdeTracker.Api.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace FdeTracker.Api.Controllers;

[Route("api/me")]
public sealed class MeController(UserService users) : ApiControllerBase
{
    // Open to people still waiting for approval, so the UI can tell them so.
    [HttpGet]
    public IActionResult Get() => Ok(UserView.From(CurrentUser));

    [HttpPatch, RequireApproved]
    public async Task<IActionResult> Update(UpdateMeRequest body) => Ok(await users.UpdateProfile(CurrentUser.Id, body));
}
