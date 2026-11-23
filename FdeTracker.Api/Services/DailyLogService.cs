using FdeTracker.Api.Common;
using FdeTracker.Api.Data;
using FdeTracker.Api.Repositories;

namespace FdeTracker.Api.Services;

public sealed class LogCell { public string Task { get; set; } = ""; public string Date { get; set; } = ""; public decimal Hours { get; set; } public string? Note { get; set; } }
public sealed class LogWeek { public string WeekStart { get; set; } = ""; public List<LogCell> Cells { get; set; } = []; public DateTime? SubmittedAt { get; set; } public decimal Total { get; set; } }
public sealed class LogSummaryRow { public Guid UserId { get; set; } public string Name { get; set; } = ""; public string? BatchName { get; set; } public decimal Hours { get; set; } public int DaysLogged { get; set; } public DateTime? SubmittedAt { get; set; } }

/// <summary>The daily log: tasks as rows, days (Monday to Friday) as columns, hours and a note in each cell. A person edits only their own week;
/// reviewers and the admin read everyone's and see who has not logged or submitted.</summary>
public sealed class DailyLogService(Db db, AuditRepository audit, NotificationService notifications)
{
    public const int MaxCells = 100;
    sealed class CellRow { public string Task { get; set; } = ""; public DateTime LogDate { get; set; } public decimal Hours { get; set; } public string? Note { get; set; } }

    public static DateOnly ParseMonday(string s)
    {
        if (!DateOnly.TryParseExact(s, "yyyy-MM-dd", out var d)) throw new ApiException(400, "The week must be a date like 2026-11-23.");
        if (d.DayOfWeek != DayOfWeek.Monday) throw new ApiException(400, "The week must start on a Monday.");
        return d;
    }

    public Task<LogWeek> Get(Guid userId, DateOnly monday) => db.Read(s => Load(s, userId, monday));

    static async Task<LogWeek> Load(Session s, Guid userId, DateOnly monday)
    {
        var rows = await s.Query<CellRow>("select task, log_date, hours, note from daily_log where user_id = @userId and log_date between @a and @b order by task, log_date",
            new { userId, a = monday.ToDateTime(TimeOnly.MinValue), b = monday.AddDays(6).ToDateTime(TimeOnly.MinValue) });
        var sub = await s.Scalar<DateTime?>("select submitted_at from daily_log_weeks where user_id = @userId and week_start = @w", new { userId, w = monday.ToDateTime(TimeOnly.MinValue) });
        return new LogWeek
        {
            WeekStart = monday.ToString("yyyy-MM-dd"), SubmittedAt = sub, Total = rows.Sum(r => r.Hours),
            Cells = rows.Select(r => new LogCell { Task = r.Task, Date = DateOnly.FromDateTime(r.LogDate).ToString("yyyy-MM-dd"), Hours = r.Hours, Note = r.Note }).ToList(),
        };
    }

    /// <summary>Replaces the week's cells. Zero-hour cells with no note are dropped. A change after a submit clears the submitted mark.</summary>
    public Task<LogWeek> Save(Guid userId, DateOnly monday, List<LogCell> cells) => db.Tx(async s =>
    {
        if (cells.Count > MaxCells) throw new ApiException(400, $"A week can hold at most {MaxCells} entries.");
        var clean = new List<LogCell>();
        foreach (var c in cells)
        {
            var task = (c.Task ?? "").Trim();
            if (task.Length is < 1 or > 120) throw new ApiException(400, "Every task needs a name of 1 to 120 characters.");
            if (!DateOnly.TryParseExact(c.Date, "yyyy-MM-dd", out var d) || d < monday || d > monday.AddDays(4)) throw new ApiException(400, "Each entry must be on a weekday of that week (Monday to Friday).");
            if (c.Hours is < 0 or > 24) throw new ApiException(400, "Hours must be between 0 and 24.");
            var note = string.IsNullOrWhiteSpace(c.Note) ? null : c.Note.Trim();
            if (note is { Length: > 500 }) throw new ApiException(400, "A note can have at most 500 characters.");
            if (c.Hours == 0 && note is null) continue;
            clean.Add(new LogCell { Task = task, Date = c.Date, Hours = Math.Round(c.Hours, 2), Note = note });
        }
        if (clean.GroupBy(c => (c.Task.ToLowerInvariant(), c.Date)).Any(g => g.Count() > 1)) throw new ApiException(400, "The same task appears twice on one day.");
        foreach (var g in clean.GroupBy(c => c.Date)) if (g.Sum(c => c.Hours) > 24) throw new ApiException(400, $"{g.Key} has more than 24 hours in total.");

        var a = monday.ToDateTime(TimeOnly.MinValue); var b = monday.AddDays(6).ToDateTime(TimeOnly.MinValue);
        await s.Exec("delete from daily_log where user_id = @userId and log_date between @a and @b", new { userId, a, b });
        foreach (var c in clean)
            await s.Exec("insert into daily_log (user_id, log_date, task, hours, note) values (@userId, @d, @task, @hours, @note)",
                new { userId, d = DateOnly.ParseExact(c.Date, "yyyy-MM-dd").ToDateTime(TimeOnly.MinValue), task = c.Task, hours = c.Hours, note = c.Note });
        await s.Exec("delete from daily_log_weeks where user_id = @userId and week_start = @a", new { userId, a });
        // logging hours for a day settles that day's reminder
        await s.Exec("update notifications set read_at = now() where user_id = @userId and kind = 'daily_log_missed' and read_at is null and on_date = any(@dates)",
            new { userId, dates = clean.Where(c => c.Hours > 0).Select(c => DateOnly.ParseExact(c.Date, "yyyy-MM-dd").ToDateTime(TimeOnly.MinValue)).Distinct().ToArray() });
        return await Load(s, userId, monday);
    });

    public async Task<LogWeek> Submit(Guid userId, DateOnly monday)
    {
        var week = await SubmitCore(userId, monday);
        try { await notifications.WeekSubmitted(userId, monday); } catch { /* the week is submitted; an alert that fails must not undo it */ }
        return week;
    }

    Task<LogWeek> SubmitCore(Guid userId, DateOnly monday) => db.Tx(async s =>
    {
        var any = await s.Scalar<long>("select count(*) from daily_log where user_id = @userId and log_date between @a and @b", new { userId, a = monday.ToDateTime(TimeOnly.MinValue), b = monday.AddDays(6).ToDateTime(TimeOnly.MinValue) });
        if (any == 0) throw new ApiException(409, "Log some hours before you submit the week.");
        await s.Exec("insert into daily_log_weeks (user_id, week_start) values (@userId, @a) on conflict (user_id, week_start) do update set submitted_at = now()", new { userId, a = monday.ToDateTime(TimeOnly.MinValue) });
        await audit.Record(s, userId, "daily_log.submit", userId.ToString(), new { week = monday.ToString("yyyy-MM-dd") });
        return await Load(s, userId, monday);
    });

    /// <summary>Reviewers and the admin: for every approved person, hours and days logged that week and whether it was submitted.</summary>
    public Task<List<LogSummaryRow>> Summary(DateOnly monday) => db.Read(s => s.Query<LogSummaryRow>(
        """
        select u.id as user_id, split_part(coalesce(nullif(btrim(u.display_name), ''), u.email), '@', 1) as name, b.name as batch_name,
               coalesce(sum(l.hours), 0) as hours, count(distinct l.log_date)::int as days_logged, w.submitted_at
        from app_users u
        left join batches b on b.id = u.batch_id
        left join daily_log l on l.user_id = u.id and l.log_date between @a and @b
        left join daily_log_weeks w on w.user_id = u.id and w.week_start = @a
        where u.access_status = 'approved'
        group by u.id, b.id, w.submitted_at
        order by lower(split_part(coalesce(nullif(btrim(u.display_name), ''), u.email), '@', 1))
        limit 1000
        """, new { a = monday.ToDateTime(TimeOnly.MinValue), b = monday.AddDays(6).ToDateTime(TimeOnly.MinValue) }));

    /// <summary>Reviewers and the admin read one person's week. Null when there is no approved person with that id.</summary>
    public Task<LogWeek?> Of(Guid userId, DateOnly monday) => db.Read(async s =>
        await s.Scalar<bool>("select exists (select 1 from app_users where id = @userId and access_status = 'approved')", new { userId }) ? await Load(s, userId, monday) : null);
}
