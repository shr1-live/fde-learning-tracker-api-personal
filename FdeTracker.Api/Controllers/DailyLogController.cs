using FdeTracker.Api.Common;
using FdeTracker.Api.Filters;
using FdeTracker.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace FdeTracker.Api.Controllers;

public sealed class SaveLogRequest { public List<LogCell> Cells { get; set; } = []; }

/// <summary>The daily log. A person reads and writes only their own week; reviewers and the admin can read everyone's.</summary>
[Route("api/daily-log"), RequireApproved]
public sealed class DailyLogController(DailyLogService log) : ApiControllerBase
{
    [HttpGet("{monday}")]
    public async Task<IActionResult> Mine(string monday) => Ok(await log.Get(CurrentUser.Id, DailyLogService.ParseMonday(monday)));

    [HttpPut("{monday}")]
    public async Task<IActionResult> Save(string monday, SaveLogRequest body) => Ok(await log.Save(CurrentUser.Id, DailyLogService.ParseMonday(monday), body.Cells ?? []));

    [HttpPost("{monday}/submit")]
    public async Task<IActionResult> Submit(string monday) => Ok(await log.Submit(CurrentUser.Id, DailyLogService.ParseMonday(monday)));

    [HttpGet("summary/{monday}"), RequireRole("reviewer", "admin")]
    public async Task<IActionResult> Summary(string monday) => Ok(await log.Summary(DailyLogService.ParseMonday(monday)));

    [HttpGet("people/{id:guid}/{monday}"), RequireRole("reviewer", "admin")]
    public async Task<IActionResult> Person(Guid id, string monday) =>
        await log.Of(id, DailyLogService.ParseMonday(monday)) is { } week ? Ok(week) : throw new ApiException(404, "There is no such person.");
}
