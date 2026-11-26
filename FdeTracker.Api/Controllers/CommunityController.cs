using System.ComponentModel.DataAnnotations;
using FdeTracker.Api.Filters;
using FdeTracker.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace FdeTracker.Api.Controllers;

public sealed class PeerCommentRequest { [Required, StringLength(1000, MinimumLength = 1)] public string Comment { get; set; } = ""; }
public sealed class LeaderboardRequest { public bool On { get; set; } }
public sealed class InviteRequest { [Required, MaxLength(200)] public List<string> Emails { get; set; } = []; public Guid? BatchId { get; set; } }

/// <summary>Peer comments inside a batch, the opt-in leaderboard, and email invites (admin).</summary>
[RequireApproved]
public sealed class CommunityController(CommunityService community) : ApiControllerBase
{
    [HttpGet("peer/queue")]
    public async Task<IActionResult> Queue() => Ok(await community.Queue(CurrentUser));

    [HttpPost("peer/{submissionId:guid}/comment")]
    public async Task<IActionResult> Comment(Guid submissionId, PeerCommentRequest body) { await community.Comment(CurrentUser, submissionId, body.Comment); return NoContent(); }

    [HttpGet("peer/mine/{taskKey}")]
    public async Task<IActionResult> OnMine([RegularExpression("^[a-z0-9-]{3,60}\\z")] string taskKey) => Ok(await community.OnMine(CurrentUser, taskKey));

    [HttpGet("leaderboard")]
    public async Task<IActionResult> Leaderboard() => Ok(await community.Leaderboard(CurrentUser));

    [HttpPut("leaderboard/me")]
    public async Task<IActionResult> Join(LeaderboardRequest body) { await community.SetLeaderboard(CurrentUser, body.On); return NoContent(); }

    [HttpGet("admin/invites"), RequireRole("admin")]
    public async Task<IActionResult> Invites() => Ok(await community.Invites());

    [HttpPost("admin/invites"), RequireRole("admin")]
    public async Task<IActionResult> Invite(InviteRequest body)
    {
        var r = await community.Invite(CurrentUser, body.Emails, body.BatchId);
        return Ok(new { added = r.Added, existing = r.Existing, rejected = r.Rejected });
    }
}
