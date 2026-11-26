using System.Globalization;
using System.Net.Http.Json;
using FdeTracker.Api.Common;
using FdeTracker.Api.Data;
using FdeTracker.Api.Models;

namespace FdeTracker.Api.Services;

public sealed class NotificationView { public Guid Id { get; set; } public string Kind { get; set; } = ""; public string OnDate { get; set; } = ""; public string Title { get; set; } = ""; public string Body { get; set; } = ""; public string? Link { get; set; } public DateTime CreatedAt { get; set; } public bool Read { get; set; } }
public sealed class NotificationList { public List<NotificationView> Items { get; set; } = []; public int Unread { get; set; } }

/// <summary>
/// Notifications. At <see cref="AppOptions.ReminderHour"/> (company time zone) every learner in a batch who logged no hours on the previous working day
/// gets one reminder, in the app and (when email is set up) by email. When a person submits their week, reviewers and the admin are told, in the app,
/// by email and (when a webhook is set) in Slack or Teams. Email and the webhook are optional: without them only the in-app part runs.
/// </summary>
public sealed class NotificationService(Db db, AppOptions options, IHttpClientFactory http, ILogger<NotificationService> log)
{
    sealed class Created { public Guid Id { get; set; } public string Email { get; set; } = ""; public string Name { get; set; } = ""; public string OnDate { get; set; } = ""; }

    const string NameSql = "split_part(coalesce(nullif(btrim(u.display_name), ''), u.email), '@', 1)";

    /// <summary>The company time zone. Invariant globalization may have no time zone data, and India has no daylight saving, so a fixed UTC+05:30 is exact.</summary>
    public static TimeZoneInfo ResolveZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win)) { try { return TimeZoneInfo.FindSystemTimeZoneById(win); } catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { } }
        return id is "Asia/Kolkata" or "Asia/Calcutta"
            ? TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "India Standard Time", "India Standard Time")
            : throw new InvalidOperationException($"REMINDER_TIME_ZONE '{id}' is not known on this server");
    }

    /// <summary>The working day a reminder at <paramref name="nowUtc"/> is about, or null before the reminder hour or at weekends. Monday reminds about Friday.</summary>
    public static DateOnly? DayToRemind(DateTime nowUtc, TimeZoneInfo zone, int hour)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || local.Hour < hour) return null;
        var d = DateOnly.FromDateTime(local).AddDays(-1);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(-1);
        return d;
    }

    /// <summary>Creates the due reminders (for everyone, or one person) and emails the new ones. Returns how many were created.</summary>
    public async Task<int> RunReminders(Guid? onlyUserId = null, DateTime? nowUtc = null)
    {
        if (nowUtc is null && !options.RemindersEnabled) return 0; // a fixed clock (tests) runs even when the timer is off
        if (onlyUserId is null) { try { await WeeklyDigest(nowUtc ?? DateTime.UtcNow); } catch (Exception e) { log.LogWarning(e, "Weekly digest failed"); } }
        if (DayToRemind(nowUtc ?? DateTime.UtcNow, ResolveZone(options.ReminderTimeZone), options.ReminderHour) is not { } day) return 0;
        var iso = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var created = await db.Tx(s => s.Query<Created>(
            $"""
            with missed as (
              select u.id from app_users u
              where u.access_status = 'approved' and u.role = 'member' and u.batch_id is not null
                and u.created_at::date <= @d::date
                and (@only::uuid is null or u.id = @only::uuid)
                and not exists (select 1 from daily_log l where l.user_id = u.id and l.log_date = @d::date and l.hours > 0)
            ), added as (
              insert into tracker_notifications (user_id, kind, on_date, title, body, link)
              select id, 'daily_log_missed', @d::date, 'Fill in your daily log',
                     'You have not logged any hours for ' || to_char(@d::date, 'Dy DD Mon') || '.', '/daily-log'
              from missed on conflict (user_id, kind, on_date, ref) do nothing
              returning id, user_id
            )
            select a.id, u.email, {NameSql} as name, @d as on_date from added a join app_users u on u.id = a.user_id
            """, new { d = iso, only = onlyUserId }));
        foreach (var r in created)
            await Email(r.Id, r.Email, $"Reminder: your daily log for {Pretty(r.OnDate)}", $"Hi {r.Name},\n\nYou have not logged any hours for {Pretty(r.OnDate)}.\nFill in your daily log: {Url("/daily-log")}\n\nFDE Training");
        if (created.Count > 0) log.LogInformation("Daily log reminders for {Day}: {Count}", iso, created.Count);
        return created.Count;
    }

    /// <summary>
    /// On Monday from the reminder hour: one summary of last week (Monday to Friday) for every admin, in the app and by email.
    /// Counts what was handed in and approved, how many learners logged hours and how many logged nothing. One per admin per Monday.
    /// </summary>
    public async Task<int> WeeklyDigest(DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, ResolveZone(options.ReminderTimeZone));
        if (local.DayOfWeek != DayOfWeek.Monday || local.Hour < options.ReminderHour) return 0;
        var thisMonday = DateOnly.FromDateTime(local);
        var from = thisMonday.AddDays(-7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = thisMonday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var rows = await db.Tx(s => s.Query<Created>(
            $"""
            with stats as (
              select (select count(*) from submissions x where x.submitted_at >= @from::date and x.submitted_at < @to::date) as handed,
                     (select count(*) from submissions x where x.status = 'approved' and x.reviewed_at >= @from::date and x.reviewed_at < @to::date) as approved,
                     (select count(distinct l.user_id) from daily_log l join app_users u on u.id = l.user_id where l.log_date >= @from::date and l.log_date < @to::date and l.hours > 0 and u.role = 'member') as logged,
                     (select count(*) from app_users u where u.access_status = 'approved' and u.role = 'member' and u.batch_id is not null
                        and not exists (select 1 from daily_log l where l.user_id = u.id and l.log_date >= @from::date and l.log_date < @to::date and l.hours > 0)) as silent
            ), added as (
              insert into tracker_notifications (user_id, kind, on_date, title, body, link)
              select a.id, 'weekly_digest', @to::date, 'Last week at a glance',
                     st.handed || ' days handed in, ' || st.approved || ' approved. ' || st.logged || ' learners logged hours, ' || st.silent || ' logged nothing.',
                     '/progress?tab=cohort'
              from app_users a cross join stats st where a.access_status = 'approved' and a.role = 'admin'
              on conflict (user_id, kind, on_date, ref) do nothing
              returning id, user_id, body
            )
            select a.id, u.email, a.body as name, @to as on_date from added a join app_users u on u.id = a.user_id
            """, new { from, to }));
        foreach (var r in rows) await Email(r.Id, r.Email, "Last week at a glance", $"{r.Name}\n\nOpen the cohort dashboard: {Url("/progress?tab=cohort")}\n\nFDE Training");
        if (rows.Count > 0 && options.AlertWebhookUrl is { Length: > 0 } hook) await Webhook(hook, "Last week at a glance: " + rows[0].Name);
        return rows.Count;
    }

    /// <summary>Tells every reviewer and admin (except the person) that a week was submitted; a resubmission shows as new again.</summary>
    public async Task WeekSubmitted(Guid submitter, DateOnly monday)
    {
        var week = monday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var rows = await db.Tx(s => s.Query<Created>(
            $"""
            with who as (select {NameSql} as name from app_users u where u.id = @submitter), added as (
              insert into tracker_notifications (user_id, kind, on_date, ref, title, body, link)
              select r.id, 'week_submitted', @w::date, @submitter::text,
                     left((select name from who) || ' submitted their week', 200),
                     left((select name from who) || ' submitted the daily log for the week of ' || to_char(@w::date, 'DD Mon') || '.', 1000),
                     '/daily-log?tab=team'
              from app_users r where r.access_status = 'approved' and r.role in ('reviewer', 'admin') and r.id <> @submitter
              on conflict (user_id, kind, on_date, ref) do update set created_at = now(), read_at = null, title = excluded.title, body = excluded.body, emailed_at = null
              returning id, user_id
            )
            select a.id, u.email, (select name from who) as name, @w as on_date from added a join app_users u on u.id = a.user_id
            """, new { submitter, w = week }));
        foreach (var r in rows)
            await Email(r.Id, r.Email, $"{r.Name} submitted their week", $"{r.Name} submitted the daily log for the week of {Pretty(r.OnDate)}.\nOpen it: {Url("/daily-log?tab=team")}\n\nFDE Training");
        if (rows.Count > 0 && options.AlertWebhookUrl is { Length: > 0 } hook) await Webhook(hook, $"{rows[0].Name} submitted their daily log for the week of {Pretty(week)}. {Url("/daily-log?tab=team")}");
    }

    static string Pretty(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dddd d MMM", CultureInfo.InvariantCulture);
    string Url(string path) => options.AppUrl.TrimEnd('/') + path;

    /// <summary>Optional. A failure is only logged: the in-app notification is already saved.</summary>
    async Task Email(Guid id, string to, string subject, string text)
    {
        if (options.ResendApiKey is not { Length: > 0 } key || options.EmailFrom is not { Length: > 0 } from) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails") { Content = JsonContent.Create(new { from, to = new[] { to }, subject, text }) };
            req.Headers.Authorization = new("Bearer", key);
            using var res = await http.CreateClient("email").SendAsync(req);
            if (res.IsSuccessStatusCode) await db.Exec($"update tracker_notifications set emailed_at = now() where id = '{id}'");
            else log.LogWarning("Notification email failed with {Status}", (int)res.StatusCode);
        }
        catch (Exception e) { log.LogWarning(e, "Notification email failed"); }
    }

    async Task Webhook(string url, string text)
    {
        try { using var res = await http.CreateClient("email").PostAsJsonAsync(url, new { text }); if (!res.IsSuccessStatusCode) log.LogWarning("Alert webhook failed with {Status}", (int)res.StatusCode); }
        catch (Exception e) { log.LogWarning(e, "Alert webhook failed"); }
    }

    public async Task<NotificationList> List(AppUser user)
    {
        try { await RunReminders(user.Id); } catch (Exception e) { log.LogWarning(e, "Reminder catch-up failed"); } // catches up when the free server slept through 10:00
        return await db.Read(async s => new NotificationList
        {
            Items = await s.Query<NotificationView>("select id, kind, to_char(on_date, 'YYYY-MM-DD') as on_date, title, body, link, created_at, (read_at is not null) as read from tracker_notifications where user_id = @id order by created_at desc, id desc limit 30", new { id = user.Id }),
            Unread = await s.Scalar<int>("select count(*)::int from tracker_notifications where user_id = @id and read_at is null", new { id = user.Id }),
        });
    }

    public Task MarkRead(AppUser user, Guid id) => db.Read(s => s.Exec("update tracker_notifications set read_at = now() where id = @id and user_id = @u and read_at is null", new { id, u = user.Id }));
    public Task MarkAllRead(AppUser user) => db.Read(s => s.Exec("update tracker_notifications set read_at = now() where user_id = @u and read_at is null", new { u = user.Id }));
}

/// <summary>Checks every few minutes whether reminders are due. Cheap when nothing is: the unique index makes repeats no-ops.</summary>
public sealed class ReminderJob(NotificationService service, ILogger<ReminderJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try { await service.RunReminders(); } catch (Exception e) { log.LogWarning(e, "Reminder run failed; trying again in 5 minutes"); }
        } while (await timer.WaitForNextTickAsync(stop));
    }
}
