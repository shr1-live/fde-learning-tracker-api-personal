using FdeTracker.Api.Common;
using FdeTracker.Api.Data;
using FdeTracker.Api.Models;
using FdeTracker.Api.Repositories;

namespace FdeTracker.Api.Services;

public sealed class PeerItem { public Guid Id { get; set; } public string TaskKey { get; set; } = ""; public string Author { get; set; } = ""; public string? Note { get; set; } public string? Learned { get; set; } public DateTime? SubmittedAt { get; set; } }
public sealed class PeerCommentView { public string Author { get; set; } = ""; public string Comment { get; set; } = ""; public DateTime CreatedAt { get; set; } }
public sealed class BoardRow { public Guid UserId { get; set; } public string Name { get; set; } = ""; public int Approved { get; set; } public int HandedIn { get; set; } public bool Me { get; set; } }
public sealed class Board { public bool On { get; set; } public bool InBatch { get; set; } public List<BoardRow> Rows { get; set; } = []; }
public sealed class InviteRow { public string Email { get; set; } = ""; public string? BatchName { get; set; } public DateTime CreatedAt { get; set; } public bool Used { get; set; } }

/// <summary>
/// Peer review (classmates in the same batch comment on each other's submitted work), the opt-in leaderboard, and email invites.
/// Peer comments never change a submission's status: only reviewers decide. The leaderboard shows only people who switched it on, only to their own batch.
/// </summary>
public sealed class CommunityService(Db db, AuditRepository audit, AppOptions options)
{
    const string Name = "split_part(coalesce(nullif(btrim(u.display_name), ''), u.email), '@', 1)";

    // ---- peer review
    /// <summary>Other people's submitted work in my batch that I have not commented on yet. Empty when I am in no batch.</summary>
    public Task<List<PeerItem>> Queue(AppUser me) => db.Read(s => me.BatchId is null ? Task.FromResult<List<PeerItem>>([]) : s.Query<PeerItem>(
        $"""
        select x.id, x.task_key, {Name} as author, x.note, x.learned, x.submitted_at
        from submissions x join app_users u on u.id = x.user_id
        where u.batch_id = @batch and x.user_id <> @me and x.status in ('submitted', 'approved', 'changes_requested')
          and not exists (select 1 from peer_comments p where p.submission_id = x.id and p.author_id = @me)
        order by x.submitted_at desc nulls last limit 50
        """, new { batch = me.BatchId, me = me.Id }));

    public Task<int> Comment(AppUser me, Guid submissionId, string comment) => db.Tx(async s =>
    {
        var text = (comment ?? "").Trim();
        if (text.Length is < 1 or > 1000) throw ApiException.BadRequest("A comment needs 1 to 1000 characters");
        var row = await s.First<(Guid UserId, string Status, Guid? BatchId)?>(
            "select x.user_id, x.status, u.batch_id from submissions x join app_users u on u.id = x.user_id where x.id = @id", new { id = submissionId });
        if (row is null || row.Value.Status == "draft") throw ApiException.NotFound();
        if (row.Value.UserId == me.Id) throw new ApiException(403, "You cannot comment on your own work");
        if (me.BatchId is null || row.Value.BatchId != me.BatchId) throw new ApiException(403, "You can comment on work from your own batch only");
        await s.Exec("insert into peer_comments (submission_id, author_id, comment) values (@id, @me, @text) on conflict (submission_id, author_id) do update set comment = excluded.comment, created_at = now()", new { id = submissionId, me = me.Id, text });
        await audit.Record(s, me.Id, "peer.comment", submissionId.ToString());
        return 0;
    });

    /// <summary>Classmates' comments on my own work for one day.</summary>
    public Task<List<PeerCommentView>> OnMine(AppUser me, string taskKey) => db.Read(s => s.Query<PeerCommentView>(
        $"""
        select {Name} as author, p.comment, p.created_at
        from peer_comments p join submissions x on x.id = p.submission_id join app_users u on u.id = p.author_id
        where x.user_id = @me and x.task_key = @taskKey and x.status <> 'draft' order by p.created_at desc
        """, new { me = me.Id, taskKey }));

    // ---- leaderboard
    public Task<int> SetLeaderboard(AppUser me, bool on) => db.Tx(async s =>
    {
        await s.Exec("update app_users set leaderboard_opt_in = @on where id = @me", new { on, me = me.Id });
        await audit.Record(s, me.Id, on ? "leaderboard.join" : "leaderboard.leave", me.Id.ToString());
        return 0;
    });

    public Task<Board> Leaderboard(AppUser me) => db.Read(async s =>
    {
        var on = await s.Scalar<bool>("select leaderboard_opt_in from app_users where id = @me", new { me = me.Id });
        if (me.BatchId is null) return new Board { On = on, InBatch = false };
        var rows = await s.Query<BoardRow>(
            $"""
            select u.id as user_id, {Name} as name, (u.id = @me) as me,
                   count(x.id) filter (where x.status = 'approved')::int as approved,
                   count(x.id) filter (where x.status in ('submitted', 'approved', 'changes_requested'))::int as handed_in
            from app_users u left join submissions x on x.user_id = u.id
            where u.batch_id = @batch and u.access_status = 'approved' and u.leaderboard_opt_in and u.role = 'member'
            group by u.id order by approved desc, handed_in desc, lower({Name}) limit 100
            """, new { batch = me.BatchId, me = me.Id });
        return new Board { On = on, InBatch = true, Rows = rows };
    });

    // ---- invites (admin)
    public Task<List<InviteRow>> Invites() => db.Read(s => s.Query<InviteRow>(
        "select i.email, b.name as batch_name, i.created_at, (i.used_at is not null) as used from invites i left join batches b on b.id = i.batch_id order by i.created_at desc, i.email limit 500"));

    /// <summary>Invites a list of office email addresses. People who already have an account are approved (and moved into the batch) at once.</summary>
    public Task<(int Added, int Existing, List<string> Rejected)> Invite(AppUser admin, List<string> emails, Guid? batchId) => db.Tx(async s =>
    {
        if (emails.Count == 0 || emails.Count > 200) throw ApiException.BadRequest("Send between 1 and 200 email addresses");
        if (batchId is not null && !await s.Scalar<bool>("select exists (select 1 from batches where id = @id)", new { id = batchId })) throw ApiException.BadRequest("Unknown batch");
        var domain = "@" + options.AllowedDomain;
        var rejected = new List<string>(); int added = 0, existing = 0;
        foreach (var e in emails.Select(x => (x ?? "").Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct())
        {
            if (e.Length > 200 || !e.EndsWith(domain) || e.IndexOf('@') != e.Length - domain.Length || e.IndexOf('@') < 1 || e.Contains(' ')) { rejected.Add(e); continue; }
            var user = await s.First<Guid?>("select id from app_users where email = @e", new { e });
            if (user is not null)
            {
                await s.Exec("update app_users set access_status = 'approved', batch_id = coalesce(@batchId, batch_id) where id = @id and role <> 'admin'", new { id = user, batchId });
                existing++;
            }
            else
            {
                await s.Exec("insert into invites (email, batch_id, created_by) values (@e, @batchId, @by) on conflict (email) do update set batch_id = excluded.batch_id, used_at = null", new { e, batchId, by = admin.Id });
                added++;
            }
        }
        await audit.Record(s, admin.Id, "invite.bulk", "invites", new { added, existing, rejected = rejected.Count });
        return (added, existing, rejected);
    });
}
