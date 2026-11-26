namespace FdeTracker.Tests;

/// <summary>The daily log (tasks as rows, weekdays as columns, hours and notes), the reviewers' summary and the admin's audit trail. Its own database (own fixture).</summary>
[TestCaseOrderer("FdeTracker.Tests.NameOrderer", "FdeTracker.Tests")]
[Collection(ApiFixture.Collection)]
public sealed class DailyLogTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    static readonly TestUser Admin = new(ApiFixture.Id(1), "boss@fiftyfivetech.io");
    static readonly TestUser M1 = new(ApiFixture.Id(601), "asha.verma@fiftyfivetech.io");
    static readonly TestUser M2 = new(ApiFixture.Id(602), "karan.singh@fiftyfivetech.io");
    static readonly TestUser R = new(ApiFixture.Id(603), "reviewer.r@fiftyfivetech.io");
    const string Week = "2026-11-23"; // a Monday

    static object Cell(string task, string date, decimal hours, string? note = null) => new { task, date, hours, note };
    Task<ApiResult> Put(TestUser u, string week, params object[] cells) => api.Call("PUT", $"/api/daily-log/{week}", u, new { cells });

    [Fact]
    public async Task L01_a_person_saves_and_reads_their_own_week()
    {
        foreach (var u in new[] { Admin, M1, M2, R }) Assert.Equal(200, (await api.Call("GET", "/api/me", u)).Status);
        Assert.Equal(200, (await api.Call("POST", $"/api/admin/users/{R.Id}/role", Admin, new { role = "reviewer" })).Status);
        Assert.Equal(200, (await api.Call("PATCH", "/api/me", M1, new { display_name = "Asha" })).Status);

        var empty = await api.Call("GET", $"/api/daily-log/{Week}", M1);
        Assert.Equal(200, empty.Status);
        Assert.Empty(empty.Body["cells"]!.AsArray());

        var saved = await Put(M1, Week, Cell("Prompt engineering", "2026-11-23", 3, "wrote the system prompt"), Cell("Prompt engineering", "2026-11-24", 2.5m), Cell("Evals", "2026-11-24", 1),
            Cell("Empty cell is dropped", "2026-11-25", 0));
        Assert.Equal(200, saved.Status);
        Assert.Equal(3, saved.Body["cells"]!.AsArray().Count);
        Assert.Equal(6.5m, (decimal)saved.Body["total"]!);
        Assert.Null(saved.Body["submitted_at"]);

        // other people never see it through their own endpoint
        Assert.Empty((await api.Call("GET", $"/api/daily-log/{Week}", M2)).Body["cells"]!.AsArray());
    }

    [Fact]
    public async Task L02_bad_input_is_refused()
    {
        Assert.Equal(400, (await api.Call("GET", "/api/daily-log/2026-11-24", M1)).Status);                         // not a Monday
        Assert.Equal(400, (await api.Call("GET", "/api/daily-log/soon", M1)).Status);
        Assert.Equal(400, (await Put(M1, Week, Cell("x", "2026-11-28", 1))).Status);                                // a Saturday
        Assert.Equal(400, (await Put(M1, Week, Cell("x", "2026-11-30", 1))).Status);                                // next week
        Assert.Equal(400, (await Put(M1, Week, Cell("x", "2026-11-23", 25))).Status);
        Assert.Equal(400, (await Put(M1, Week, Cell("x", "2026-11-23", -1))).Status);
        Assert.Equal(400, (await Put(M1, Week, Cell("", "2026-11-23", 1))).Status);
        Assert.Equal(400, (await Put(M1, Week, Cell("same", "2026-11-23", 1), Cell("SAME", "2026-11-23", 2))).Status);   // same task twice on a day
        Assert.Equal(400, (await Put(M1, Week, Cell("a", "2026-11-23", 20), Cell("b", "2026-11-23", 5))).Status);         // over 24 hours in a day
        Assert.Equal(401, (await api.Call("GET", $"/api/daily-log/{Week}")).Status);
        Assert.Equal(200, (await api.Call("GET", $"/api/daily-log/{Week}", M1)).Status);
        Assert.Equal(3, (await api.Call("GET", $"/api/daily-log/{Week}", M1)).Body["cells"]!.AsArray().Count);          // the failed saves changed nothing
    }

    [Fact]
    public async Task L03_submitting_marks_the_week_and_a_later_edit_clears_the_mark()
    {
        Assert.Equal(409, (await api.Call("POST", $"/api/daily-log/{Week}/submit", M2)).Status);   // nothing logged yet
        var sub = await api.Call("POST", $"/api/daily-log/{Week}/submit", M1);
        Assert.Equal(200, sub.Status);
        Assert.NotNull(sub.Body["submitted_at"]);
        var edited = await Put(M1, Week, Cell("Evals", "2026-11-24", 1.5m));
        Assert.Null(edited.Body["submitted_at"]);
        Assert.Equal(1, edited.Body["cells"]!.AsArray().Count);
        Assert.Equal(200, (await api.Call("POST", $"/api/daily-log/{Week}/submit", M1)).Status);
    }

    [Fact]
    public async Task L04_reviewers_and_the_admin_see_everyones_week_members_do_not()
    {
        Assert.Equal(200, (await Put(M2, Week, Cell("RAG", "2026-11-23", 4))).Status);
        Assert.Equal(403, (await api.Call("GET", $"/api/daily-log/summary/{Week}", M1)).Status);
        Assert.Equal(403, (await api.Call("GET", $"/api/daily-log/people/{M2.Id}/{Week}", M1)).Status);
        foreach (var viewer in new[] { R, Admin })
        {
            var list = (await api.Call("GET", $"/api/daily-log/summary/{Week}", viewer)).Body.AsArray();
            var asha = list.Single(p => (string)p!["name"]! == "Asha")!;
            Assert.Equal(1.5m, (decimal)asha["hours"]!);
            Assert.NotNull(asha["submitted_at"]);
            var karan = list.Single(p => (string)p!["name"]! == "karan.singh")!;
            Assert.Equal(4m, (decimal)karan["hours"]!);
            Assert.Null(karan["submitted_at"]);
            Assert.DoesNotContain("@", list.ToJsonString());
            Assert.Equal(1, (await api.Call("GET", $"/api/daily-log/people/{M2.Id}/{Week}", viewer)).Body["cells"]!.AsArray().Count);
        }
        Assert.Equal(404, (await api.Call("GET", $"/api/daily-log/people/{Guid.NewGuid()}/{Week}", R)).Status);
    }

    [Fact]
    public async Task L05_the_admin_reads_the_audit_trail_nobody_else_does()
    {
        Assert.Equal(403, (await api.Call("GET", "/api/admin/audit", R)).Status);
        Assert.Equal(403, (await api.Call("GET", "/api/admin/audit", M1)).Status);
        var rows = (await api.Call("GET", "/api/admin/audit?limit=50", Admin)).Body.AsArray();
        Assert.Contains(rows, r => (string)r!["action"]! == "daily_log.submit");
        Assert.DoesNotContain("@", rows.ToJsonString());
    }
}
