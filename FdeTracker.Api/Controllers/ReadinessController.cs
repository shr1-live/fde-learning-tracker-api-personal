using FdeTracker.Api.Filters;
using FdeTracker.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace FdeTracker.Api.Controllers;

/// <summary>Everyone's readiness self-ratings. Reviewers and the admin only (members get 403); a person's own ratings stay in the web app.</summary>
[Route("api/readiness"), RequireApproved, RequireRole("reviewer", "admin")]
public sealed class ReadinessController(ReadinessService readiness) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Everyone([FromQuery] Guid? batch) => Ok(await readiness.Everyone(batch));
}
