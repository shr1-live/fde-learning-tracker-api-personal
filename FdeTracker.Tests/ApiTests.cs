using System.Text.Json.Nodes;

namespace FdeTracker.Tests;

[TestCaseOrderer("FdeTracker.Tests.NameOrderer", "FdeTracker.Tests")]
public sealed class ApiTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    static readonly TestUser Admin = new(ApiFixture.Id(1), "boss@fiftyfivetech.io");
    static readonly TestUser B = new(ApiFixture.Id(2), "b@fiftyfivetech.io");
    static readonly TestUser C = new(ApiFixture.Id(3), "c@fiftyfivetech.io");
    static readonly TestUser D = new(ApiFixture.Id(4), "d@fiftyfivetech.io");
    static readonly TestUser E = new(ApiFixture.Id(5), "e@fiftyfivetech.io"); // 5th person: no free seat left
    static readonly TestUser Outsider = new(ApiFixture.Id(9), "x@gmail.com");

    static string[] Titles(JsonNode? items) => items!.AsArray().Select(i => (string)i!["title"]!).ToArray();

    [Fact]
    public async Task T01_public_endpoints_and_security_headers()
    {
        Assert.Equal(200, (await api.Call("GET", "/health")).Status);
        Assert.Equal(4, (await api.Call("GET", "/api/stacks")).Body.AsArray().Count);
        Assert.True((await api.Call("GET", "/api/topics")).Body.AsArray().Count >= 10);
        var r = await api.Raw("/health");
        Assert.False(r.Headers.Contains("X-Powered-By"));
        Assert.False(r.Headers.Contains("Server"));
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task T02_authentication_refuses_no_token_bad_token_and_wrong_domain()
    {
        Assert.Equal(401, (await api.Call("GET", "/api/me")).Status);
        Assert.Equal(401, (await api.Call("GET", "/api/me", rawToken: "nope")).Status);
        Assert.Equal(403, (await api.Call("GET", "/api/me", Outsider)).Status);
    }

    [Fact]
    public async Task T03_admin_and_first_three_members_are_approved_and_the_fifth_person_waits()
    {
        Assert.Equal("admin", (string)(await api.Call("GET", "/api/me", Admin)).Body["role"]!);
        foreach (var u in new[] { B, C, D }) Assert.Equal("approved", (string)(await api.Call("GET", "/api/me", u)).Body["access"]!);
        Assert.Equal("pending", (string)(await api.Call("GET", "/api/me", E)).Body["access"]!);
        Assert.Equal(403, (await api.Call("GET", "/api/feed", E)).Status);
        Assert.Equal(403, (await api.Call("POST", "/api/content", E, new { })).Status);
    }

    [Fact]
    public async Task T04_only_the_admin_can_approve_people_or_change_roles()
    {
        Assert.Equal(403, (await api.Call("GET", "/api/admin/users", B)).Status);
        Assert.Equal(403, (await api.Call("POST", $"/api/admin/users/{E.Id}/access", B, new { status = "approved" })).Status);
        Assert.Equal(400, (await api.Call("POST", $"/api/admin/users/{Admin.Id}/access", Admin, new { status = "rejected" })).Status);
        Assert.Equal(200, (await api.Call("POST", $"/api/admin/users/{E.Id}/access", Admin, new { status = "approved" })).Status);
        Assert.Equal(200, (await api.Call("GET", "/api/feed", E)).Status);
        Assert.Equal(404, (await api.Call("POST", $"/api/admin/users/{Admin.Id}/role", Admin, new { role = "member" })).Status);
        Assert.Equal(200, (await api.Call("POST", $"/api/admin/users/{C.Id}/role", Admin, new { role = "reviewer" })).Status);
    }

    [Fact]
    public async Task T05_content_review_flow_nothing_is_public_until_approved_and_stacks_filter()
    {
        await api.Call("PATCH", "/api/me", B, new { stack_id = "node" });
        await api.Call("PATCH", "/api/me", D, new { stack_id = "python" });
        var general = await api.Call("POST", "/api/content", B, new { topic = "rag", stack_id = (string?)null, kind = "article", title = "RAG in plain words", body_md = "Retrieve, then generate.", submit = true });
        var py = await api.Call("POST", "/api/content", B, new { topic = "rag", stack_id = "python", kind = "link", title = "Python RAG tutorial", url = "https://example.com/py", submit = true });
        Assert.Equal(201, general.Status);
        Assert.Equal("in_review", (string)general.Body["status"]!);
        string gid = (string)general.Body["id"]!, pid = (string)py.Body["id"]!;

        Assert.Empty((await api.Call("GET", "/api/feed", D)).Body["items"]!.AsArray());
        Assert.Equal(404, (await api.Call("GET", $"/api/content/{gid}", D)).Status);
        Assert.Equal(200, (await api.Call("GET", $"/api/content/{gid}", B)).Status);

        Assert.Equal(403, (await api.Call("GET", "/api/review/queue", D)).Status);
        Assert.Equal(2, (await api.Call("GET", "/api/review/queue", C)).Body.AsArray().Count);

        Assert.Equal(403, (await api.Call("POST", $"/api/review/{gid}", D, new { decision = "approve" })).Status);
        Assert.Equal(400, (await api.Call("POST", $"/api/review/{gid}", C, new { decision = "reject" })).Status);
        Assert.Equal(200, (await api.Call("POST", $"/api/review/{gid}", C, new { decision = "approve" })).Status);
        Assert.Equal(200, (await api.Call("POST", $"/api/review/{pid}", C, new { decision = "approve" })).Status);
        Assert.Equal(409, (await api.Call("POST", $"/api/review/{pid}", C, new { decision = "approve" })).Status);

        Assert.Equal(new[] { "Python RAG tutorial", "RAG in plain words" }, Titles((await api.Call("GET", "/api/feed", D)).Body["items"]).Order().ToArray());
        Assert.Equal(new[] { "RAG in plain words" }, Titles((await api.Call("GET", "/api/feed", B)).Body["items"]));
        Assert.Single((await api.Call("GET", "/api/feed?stack=general", D)).Body["items"]!.AsArray());
        Assert.Empty((await api.Call("GET", "/api/feed?topic=agents", D)).Body["items"]!.AsArray());

        Assert.Equal(409, (await api.Call("PATCH", $"/api/content/{gid}", B, new { title = "Sneaky edit" })).Status);
        var detail = await api.Call("GET", $"/api/content/{gid}", D);
        Assert.Equal(200, detail.Status);
        Assert.Null(detail.Body["author_id"]);
    }

    [Fact]
    public async Task T06_reviewers_cannot_approve_their_own_work_and_changes_requested_can_be_resubmitted()
    {
        var mine = await api.Call("POST", "/api/content", C, new { topic = "mcp", stack_id = (string?)null, kind = "note", title = "My MCP note", body_md = "text", submit = true });
        Assert.Equal(403, (await api.Call("POST", $"/api/review/{(string)mine.Body["id"]!}", C, new { decision = "approve" })).Status);
        var other = await api.Call("POST", "/api/content", B, new { topic = "mcp", stack_id = (string?)null, kind = "note", title = "Needs work", body_md = "draft", submit = true });
        string oid = (string)other.Body["id"]!;
        Assert.Equal(200, (await api.Call("POST", $"/api/review/{oid}", C, new { decision = "request_changes", comment = "Add an example" })).Status);
        var list = await api.Call("GET", "/api/content/mine", B);
        Assert.Equal("Add an example", (string)list.Body.AsArray().First(x => (string)x!["id"]! == oid)!["last_review_comment"]!);
        var fixedUp = await api.Call("PATCH", $"/api/content/{oid}", B, new { body_md = "now with an example", submit = true });
        Assert.Equal("in_review", (string)fixedUp.Body["status"]!);
        Assert.Equal(404, (await api.Call("PATCH", $"/api/content/{oid}", D, new { title = "hijack" })).Status);
    }

    [Fact]
    public async Task T07_input_validation_and_abuse_limits()
    {
        var ok = new Dictionary<string, object?> { ["topic"] = "rag", ["stack_id"] = null, ["kind"] = "article", ["title"] = "Valid title", ["body_md"] = "x" };
        Dictionary<string, object?> With(params (string, object?)[] changes) { var d = new Dictionary<string, object?>(ok); foreach (var (k, v) in changes) d[k] = v; return d; }

        Assert.Equal(400, (await api.Call("POST", "/api/content", B, With(("kind", "video")))).Status);
        Assert.Equal(400, (await api.Call("POST", "/api/content", B, With(("kind", "link"), ("url", "http://insecure.example")))).Status);
        Assert.Equal(400, (await api.Call("POST", "/api/content", B, With(("kind", "link"), ("url", "javascript:alert(1)")))).Status);
        Assert.Equal(400, (await api.Call("POST", "/api/content", B, With(("topic", "nope-topic")))).Status);
        Assert.Equal(400, (await api.Call("POST", "/api/content", B, With(("title", "x")))).Status);
        Assert.Equal(400, (await api.Call("POST", "/api/content", B, With(("extra", 1), ("topic", "'; drop table app_users;--")))).Status);
        Assert.Equal(400, (await api.Call("POST", "/api/content", B, With(("extra", 1)))).Status); // unknown fields are refused
        Assert.Equal(400, (await api.Call("GET", "/api/feed?limit=9999", B)).Status);
        Assert.Equal(400, (await api.Call("GET", "/api/feed?before=garbage", B)).Status);
        Assert.Equal(400, (await api.Call("GET", "/api/content/not-a-uuid", B)).Status);
        Assert.Equal(400, (await api.Call("POST", "/api/content", B, "{ not json")).Status);
        Assert.Equal(413, (await api.Call("POST", "/api/content", B, new { x = new string('a', 70000) })).Status);
        Assert.Equal(404, (await api.Call("GET", "/api/users/everyone", B)).Status);
        Assert.True(await api.Count("select count(*) from app_users") >= 5, "table still exists");
    }

    [Fact]
    public async Task T08_programs_shared_programme_personal_progress_and_private_plans()
    {
        var six = (await api.Call("GET", "/api/programs", B)).Body.AsArray().First(p => (string)p!["slug"]! == "fde-six-week")!;
        Assert.Equal(6, (int)six["total"]!);
        var first = (await api.Call("GET", "/api/programs/fde-six-week", B)).Body["items"]![0]!;
        string firstId = (string)first["id"]!;
        Assert.Equal(200, (await api.Call("PUT", $"/api/programs/fde-six-week/items/{firstId}/progress", B, new { done = true })).Status);
        Assert.True((bool)(await api.Call("GET", "/api/programs/fde-six-week", B)).Body["items"]![0]!["done"]!);
        Assert.False((bool)(await api.Call("GET", "/api/programs/fde-six-week", D)).Body["items"]![0]!["done"]!, "progress is per person");

        var mine = await api.Call("POST", "/api/programs", B, new { title = "My own plan" });
        Assert.Equal(201, mine.Status);
        string slug = (string)mine.Body["slug"]!;
        var step = await api.Call("POST", $"/api/programs/{slug}/items", B, new { title = "Build a demo" });
        Assert.Equal(201, step.Status);
        string stepId = (string)step.Body["id"]!;
        Assert.Equal(404, (await api.Call("GET", $"/api/programs/{slug}", D)).Status);
        Assert.Equal(404, (await api.Call("POST", $"/api/programs/{slug}/items", D, new { title = "intrude" })).Status);
        Assert.Equal(404, (await api.Call("PUT", $"/api/programs/{slug}/items/{stepId}/progress", D, new { done = true })).Status);
        Assert.Equal(404, (await api.Call("POST", "/api/programs/fde-six-week/items", B, new { title = "edit shared" })).Status);
        Assert.DoesNotContain((await api.Call("GET", "/api/programs", D)).Body.AsArray(), p => (string)p!["slug"]! == slug);
        Assert.Equal(204, (await api.Call("DELETE", $"/api/programs/{slug}/items/{stepId}", B)).Status);
    }

    [Fact]
    public async Task T09_re_creating_a_user_in_the_auth_provider_keeps_their_data()
    {
        var before = (await api.Call("GET", "/api/me", B)).Body;
        var reborn = new TestUser(ApiFixture.Id(77), B.Email);
        var after = (await api.Call("GET", "/api/me", reborn)).Body;
        Assert.Equal((string)before["email"]!, (string)after["email"]!);
        Assert.Equal((string)before["stack_id"]!, (string)after["stack_id"]!);
        Assert.Equal(reborn.Id.ToString(), (string)after["id"]!);
        Assert.True((await api.Call("GET", "/api/content/mine", reborn)).Body.AsArray().Count >= 1, "content followed the person");
    }

    [Fact]
    public async Task T10_admin_audit_trail_exists()
    {
        Assert.True(await api.Count("select count(*) from audit_log where action like 'review.%' or action like 'admin.%'") >= 5);
    }

    [Fact]
    public async Task T11_feed_pages_through_a_cursor_without_repeats()
    {
        // Three more approved general items so there are 4+ in total, then walk them two at a time.
        foreach (var n in new[] { "One", "Two", "Three" })
        {
            var made = await api.Call("POST", "/api/content", D, new { topic = "agents", stack_id = (string?)null, kind = "note", title = $"Paging note {n}", body_md = "text", submit = true });
            Assert.Equal(200, (await api.Call("POST", $"/api/review/{(string)made.Body["id"]!}", C, new { decision = "approve" })).Status);
        }
        var seen = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var r = await api.Call("GET", "/api/feed?limit=2&stack=general" + (cursor is null ? "" : "&before=" + Uri.EscapeDataString(cursor)), B);
            Assert.Equal(200, r.Status);
            seen.AddRange(Titles(r.Body["items"]));
            cursor = (string?)r.Body["next"];
            if (cursor is null) break;
        }
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(4, seen.Count);
    }

    [Fact]
    public async Task T12_cors_allows_only_the_listed_origin()
    {
        var allowed = await api.GetFromOrigin("/api/stacks", "https://app.example");
        Assert.Equal("https://app.example", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var other = await api.GetFromOrigin("/api/stacks", "https://evil.example");
        Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
