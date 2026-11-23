using System.Globalization;
using Microsoft.Extensions.DependencyInjection;

namespace FdeTracker.Tests;

/// <summary>The 10:00 daily-log reminder and the "week submitted" alert. A fixed clock is used, so nothing depends on the real time. Its own database (own fixture).</summary>
[TestCaseOrderer("FdeTracker.Tests.NameOrderer", "FdeTracker.Tests")]
[Collection(ApiFixture.Collection)]
public sealed class NotificationTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    static readonly TestUser Admin = new(ApiFixture.Id(1), "boss@fiftyfivetech.io");
    static readonly TestUser M1 = new(ApiFixture.Id(701), "asha.verma@fiftyfivetech.io");
    static readonly TestUser M2 = new(ApiFixture.Id(702), "karan.singh@fiftyfivetech.io");
    static readonly TestUser R = new(ApiFixture.Id(703), "reviewer.r@fiftyfivetech.io");

    static (DateTime nowUtc, string tuesday) NextWednesday()
    {
        var ist = TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "IST", "IST");
        var wed = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(14), DateTimeKind.Unspecified);
        while (wed.DayOfWeek != DayOfWeek.Wednesday) wed = wed.AddDays(1);
        return (TimeZoneInfo.ConvertTimeToUtc(wed.AddHours(10).AddMinutes(30), ist), wed.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task N01_day_to_remind_skips_weekends_and_early_hours_and_monday_reminds_about_friday()
    {
        var ist = FdeTracker.Api.Services.NotificationService.ResolveZone("Asia/Kolkata");
        DateTime Utc(int d, int h) => TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 11, d, h, 0, 0, DateTimeKind.Unspecified), ist);
        Assert.Equal(new DateOnly(2026, 11, 24), FdeTracker.Api.Services.NotificationService.DayToRemind(Utc(25, 10), ist, 10));   // Wed 10:00 -> Tue
        Assert.Null(FdeTracker.Api.Services.NotificationService.DayToRemind(Utc(25, 9), ist, 10));                                 // before 10:00
        Assert.Null(FdeTracker.Api.Services.NotificationService.DayToRemind(Utc(28, 11), ist, 10));                                // Saturday
        Assert.Equal(new DateOnly(2026, 11, 20), FdeTracker.Api.Services.NotificationService.DayToRemind(Utc(23, 10), ist, 10));   // Mon -> Fri
        await Task.CompletedTask;
    }

    [Fact]
    public async Task N02_a_learner_in_a_batch_with_no_hours_gets_one_reminder_and_logging_hours_settles_it()
    {
        foreach (var u in new[] { Admin, M1, M2, R }) Assert.Equal(200, (await api.Call("GET", "/api/me", u)).Status);
        Assert.Equal(200, (await api.Call("POST", $"/api/admin/users/{R.Id}/role", Admin, new { role = "reviewer" })).Status);
        var batch = await api.Call("POST", "/api/admin/batches", Admin, new { name = "Batch N", starts_on = "2026-01-05" });
        foreach (var m in new[] { M1, M2 }) Assert.Equal(200, (await api.Call("POST", $"/api/admin/users/{m.Id}/batch", Admin, new { batch_id = (string)batch.Body["id"]! })).Status);

        var (now, tue) = NextWednesday();
        var service = api.Services.GetRequiredService<FdeTracker.Api.Services.NotificationService>();
        // M2 logged hours on that Tuesday, so only M1 is reminded; the reviewer and admin are not learners in a batch.
        await api.Count($"with x as (insert into daily_log (user_id, log_date, task, hours) values ('{M2.Id}', '{tue}', 'RAG', 2) returning 1) select count(*) from x");
        Assert.Equal(1, await service.RunReminders(nowUtc: now));
        Assert.Equal(0, await service.RunReminders(nowUtc: now));   // repeating the run sends nothing twice
        Assert.Equal(1, await api.Count($"select count(*) from notifications where user_id = '{M1.Id}' and kind = 'daily_log_missed' and on_date = '{tue}'"));
        Assert.Equal(0, await api.Count($"select count(*) from notifications where user_id = '{M2.Id}'"));

        var mine = (await api.Call("GET", "/api/notifications", M1)).Body;
        Assert.Equal(1, (int)mine["unread"]!);
        Assert.Equal("/daily-log", (string)mine["items"]![0]!["link"]!);
        Assert.Empty((await api.Call("GET", "/api/notifications", M2)).Body["items"]!.AsArray().Where(i => (string)i!["kind"]! == "daily_log_missed"));   // nobody sees another person's

        // logging hours for that day settles the reminder (the monday of that week)
        var d = DateOnly.ParseExact(tue, "yyyy-MM-dd");
        var monday = d.AddDays(-(((int)d.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd");
        Assert.Equal(200, (await api.Call("PUT", $"/api/daily-log/{monday}", M1, new { cells = new[] { new { task = "Evals", date = tue, hours = 1, note = (string?)null } } })).Status);
        Assert.Equal(0, (int)(await api.Call("GET", "/api/notifications", M1)).Body["unread"]!);
    }

    [Fact]
    public async Task N03_submitting_a_week_alerts_reviewers_and_the_admin_and_they_can_mark_it_read()
    {
        var monday = "2026-11-23";
        Assert.Equal(200, (await api.Call("PUT", $"/api/daily-log/{monday}", M2, new { cells = new[] { new { task = "RAG", date = "2026-11-24", hours = 3, note = (string?)null } } })).Status);
        Assert.Equal(200, (await api.Call("POST", $"/api/daily-log/{monday}/submit", M2)).Status);
        foreach (var viewer in new[] { R, Admin })
        {
            var n = (await api.Call("GET", "/api/notifications", viewer)).Body;
            var item = n["items"]!.AsArray().Single(i => (string)i!["kind"]! == "week_submitted");
            Assert.Contains("karan.singh submitted", (string)item!["title"]!);
            Assert.DoesNotContain("@", n.ToJsonString());
            Assert.Equal(204, (await api.Call("POST", $"/api/notifications/{(string)item["id"]!}/read", viewer)).Status);
            Assert.Equal(0, (int)(await api.Call("GET", "/api/notifications", viewer)).Body["unread"]!);
        }
        Assert.Empty((await api.Call("GET", "/api/notifications", M1)).Body["items"]!.AsArray().Where(i => (string)i!["kind"]! == "week_submitted"));   // learners are not alerted
        Assert.Equal(401, (await api.Call("GET", "/api/notifications")).Status);

        // a resubmission shows as new again, but there is still one row per reviewer
        Assert.Equal(200, (await api.Call("POST", $"/api/daily-log/{monday}/submit", M2)).Status);
        Assert.Equal(1, (int)(await api.Call("GET", "/api/notifications", R)).Body["unread"]!);
        Assert.Equal(1, await api.Count($"select count(*) from notifications where user_id = '{R.Id}' and kind = 'week_submitted'"));
    }
}
