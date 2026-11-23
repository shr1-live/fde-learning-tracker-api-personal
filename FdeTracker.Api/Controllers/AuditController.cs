using FdeTracker.Api.Data;
using FdeTracker.Api.Filters;
using Microsoft.AspNetCore.Mvc;

namespace FdeTracker.Api.Controllers;

/// <summary>The audit trail (who changed a role, a batch, a review...), newest first. Admin only. Names, never email addresses.</summary>
[Route("api/admin/audit"), RequireApproved, RequireRole("admin")]
public sealed class AuditController(Db db) : ApiControllerBase
{
    public sealed class AuditRow { public long Id { get; set; } public string Actor { get; set; } = ""; public string Action { get; set; } = ""; public string Target { get; set; } = ""; public string? Detail { get; set; } public DateTime CreatedAt { get; set; } }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int limit = 100) =>
        Ok(await db.Read(s => s.Query<AuditRow>(
            """
            select a.id, split_part(coalesce(nullif(btrim(u.display_name), ''), u.email, 'system'), '@', 1) as actor, a.action, a.target, a.detail::text as detail, a.created_at
            from audit_log a left join app_users u on u.id = a.actor_id
            order by a.id desc limit @limit
            """, new { limit = Math.Clamp(limit, 1, 500) })));
}
