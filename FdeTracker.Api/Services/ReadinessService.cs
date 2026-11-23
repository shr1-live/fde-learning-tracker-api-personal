using FdeTracker.Api.Data;

namespace FdeTracker.Api.Services;

/// <summary>One person's self-ratings: skill index (0-based, as in the web app's skills table) to a 1-5 rating, on Day 1 and on Day 10.</summary>
public sealed class ReadinessPersonView
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string? BatchName { get; set; }
    public Dictionary<int, int> Base { get; set; } = [];
    public Dictionary<int, int> Final { get; set; } = [];
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Everyone's readiness self-ratings, for reviewers and the admin. The web app keeps a person's own ratings in its
/// <c>entries</c> table under the keys <c>rate:base:N</c> (Day 1) and <c>rate:final:N</c> (Day 10); that table is locked to its
/// owner by row-level security, so only this API (which connects as the table owner) can read across people. Read-only.
/// </summary>
public sealed class ReadinessService(Db db)
{
    sealed class Row { public Guid UserId { get; set; } public string Name { get; set; } = ""; public string? BatchName { get; set; } public string Key { get; set; } = ""; public string Value { get; set; } = ""; public DateTime UpdatedAt { get; set; } }

    public Task<List<ReadinessPersonView>> Everyone(Guid? batch) => db.Read(async s =>
    {
        // The entries table belongs to the web app's own setup, not to this API's migrations: if it is not there, nobody has rated yet.
        if (!await s.Scalar<bool>("select to_regclass('public.entries') is not null")) return [];
        var rows = await s.Query<Row>(
            """
            select e.user_id, split_part(coalesce(nullif(btrim(u.display_name), ''), u.email), '@', 1) as name, b.name as batch_name,
                   e.key, e."value"::text as value, e.updated_at
            from public.entries e
            join app_users u on u.id = e.user_id and u.access_status = 'approved'
            left join batches b on b.id = u.batch_id
            where e.key ~ '^rate:(base|final):[0-9]{1,3}$'
              and (@batch::uuid is null or u.batch_id = @batch::uuid)
            """, new { batch });
        var people = new Dictionary<Guid, ReadinessPersonView>();
        foreach (var r in rows)
        {
            if (!int.TryParse(r.Value.Trim('"'), out var rating) || rating is < 1 or > 5) continue; // only real ratings
            var parts = r.Key.Split(':');
            var skill = int.Parse(parts[2]);
            if (!people.TryGetValue(r.UserId, out var p)) people[r.UserId] = p = new ReadinessPersonView { UserId = r.UserId, Name = r.Name, BatchName = r.BatchName };
            (parts[1] == "base" ? p.Base : p.Final)[skill] = rating;
            if (p.UpdatedAt is null || r.UpdatedAt > p.UpdatedAt) p.UpdatedAt = r.UpdatedAt;
        }
        return people.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    });
}
