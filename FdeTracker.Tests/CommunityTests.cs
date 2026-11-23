namespace FdeTracker.Tests;

/// <summary>Scored reviews, batch-only feed items, peer comments, the opt-in leaderboard and email invites. Its own database (own fixture).
/// A1 and A2 are in batch A, B1 is in batch B, R is a reviewer, Admin is the owner account (the first three people are approved automatically).</summary>
[TestCaseOrderer("FdeTracker.Tests.NameOrderer", "FdeTracker.Tests")]
[Collection(ApiFixture.Collection)]
public sealed class CommunityTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    static readonly TestUser Admin = new(ApiFixture.Id(1), "boss@fiftyfivetech.io");
    static readonly TestUser R = new(ApiFixture.Id(801), "reviewer.r@fiftyfivetech.io");
    static readonly TestUser A1 = new(ApiFixture.Id(802), "asha.verma@fiftyfivetech.io");
    static readonly TestUser A2 = new(ApiFixture.Id(803), "karan.singh@fiftyfivetech.io");
    static readonly TestUser B1 = new(ApiFixture.Id(804), "meera.rao@fiftyfivetech.io");
    static string batchA = "", batchB = "", subA1 = "";

    async Task<string> Batch(string name) => (string)(await api.Call("POST", "/api/admin/batches", Admin, new { name, starts_on = "2026-01-05" })).Body["id"]!;

    [Fact]
    public async Task C01_setup_two_batches_and_a_submitted_day()
    {
        foreach (var u in new[] { Admin, R }) Assert.Equal(200, (await api.Call("GET", "/api/me", u)).Status);
        Assert.Equal(200, (await api.Call("POST", $"/api/admin/users/{R.Id}/role", Admin, new { role = "reviewer" })).Status);
        foreach (var u in new[] { A1, A2, B1 }) { await api.Call("GET", "/api/me", u); await api.Call("POST", $"/api/admin/users/{u.Id}/access", Admin, new { status = "approved" }); }
        batchA = await Batch("Batch A"); batchB = await Batch("Batch B");
        foreach (var (u, b) in new[] { (A1, batchA), (A2, batchA), (B1, batchB) }) Assert.Equal(200, (await api.Call("POST", $"/api/admin/users/{u.Id}/batch", Admin, new { batch_id = b })).Status);
        Assert.Equal(200, (await api.Call("PATCH", "/api/me", A1, new { display_name = "Asha" })).Status);
        Assert.Equal(200, (await api.Call("PUT", "/api/submissions/week1-day1", A1, new { note = "Built it", learned = "Tokens", rating = 3 })).Status);
        Assert.Equal(200, (await api.Call("POST", "/api/submissions/week1-day1/links", A1, new { label = "Proof", url = "https://example.com/p" })).Status);
        Assert.Equal(200, (await api.Call("POST", "/api/submissions/week1-day1/submit", A1)).Status);
        subA1 = (string)(await api.Call("GET", "/api/review/submissions?status=submitted", R)).Body.AsArray().First()!["id"]!;
    }

    [Fact]
    public async Task C02_a_review_can_carry_a_checklist_score()
    {
        var bad = await api.Call("POST", $"/api/review/submissions/{subA1}", R, new { decision = "approve", met = 7, total = 5 });
        Assert.Equal(400, bad.Status);
        Assert.Equal(400, (await api.Call("POST", $"/api/review/submissions/{subA1}", R, new { decision = "approve", met = 3 })).Status);   // both or neither
        var ok = await api.Call("POST", $"/api/review/submissions/{subA1}", R, new { decision = "request_changes", comment = "Not met: add a screenshot", met = 4, total = 6 });
        Assert.Equal(200, ok.Status);
        var rev = ok.Body["reviews"]![0]!;
        Assert.Equal(4, (int)rev["met"]!); Assert.Equal(6, (int)rev["total"]!);
        // the learner sees the score on their own copy
        Assert.Equal(4, (int)(await api.Call("GET", "/api/submissions/week1-day1", A1)).Body["reviews"]![0]!["met"]!);
        // resubmit and approve without a score: allowed
        Assert.Equal(200, (await api.Call("POST", "/api/submissions/week1-day1/submit", A1)).Status);
        Assert.Equal(200, (await api.Call("POST", $"/api/review/submissions/{subA1}", R, new { decision = "approve" })).Status);
    }

    [Fact]
    public async Task C03_peer_comments_work_inside_a_batch_only_and_never_on_your_own_work()
    {
        var q = (await api.Call("GET", "/api/peer/queue", A2)).Body.AsArray();
        Assert.Single(q);
        Assert.Equal("week1-day1", (string)q[0]!["task_key"]!);
        Assert.Equal("Asha", (string)q[0]!["author"]!);
        Assert.Empty((await api.Call("GET", "/api/peer/queue", A1)).Body.AsArray());          // my own work is not in my queue
        Assert.Empty((await api.Call("GET", "/api/peer/queue", B1)).Body.AsArray());          // another batch cannot see it

        Assert.Equal(403, (await api.Call("POST", $"/api/peer/{subA1}/comment", A1, new { comment = "great" })).Status);
        Assert.Equal(403, (await api.Call("POST", $"/api/peer/{subA1}/comment", B1, new { comment = "great" })).Status);
        Assert.Equal(400, (await api.Call("POST", $"/api/peer/{subA1}/comment", A2, new { comment = "" })).Status);
        Assert.Equal(204, (await api.Call("POST", $"/api/peer/{subA1}/comment", A2, new { comment = "Nice, the screenshot helped me." })).Status);
        Assert.Equal(204, (await api.Call("POST", $"/api/peer/{subA1}/comment", A2, new { comment = "Nice, and the token count helped." })).Status);   // one per person: updated
        Assert.Empty((await api.Call("GET", "/api/peer/queue", A2)).Body.AsArray());          // commented: leaves the queue
        var mine = (await api.Call("GET", "/api/peer/mine/week1-day1", A1)).Body.AsArray();
        Assert.Single(mine);
        Assert.Contains("token count", (string)mine[0]!["comment"]!);
        Assert.DoesNotContain("@", mine.ToJsonString());
        Assert.Empty((await api.Call("GET", "/api/peer/mine/week1-day1", A2)).Body.AsArray());  // not visible to others
        Assert.Equal("approved", (string)(await api.Call("GET", "/api/submissions/week1-day1", A1)).Body["status"]!);   // a peer comment changed nothing
    }

    [Fact]
    public async Task C04_a_batch_only_feed_item_is_seen_by_that_batch_and_reviewers_only()
    {
        object Item(string title, bool batchOnly) => new { topic = "evaluation", stack_id = (string?)null, kind = "note", title, body_md = "text", submit = true, batch_only = batchOnly };
        var made = await api.Call("POST", "/api/content", R, Item("Everyone note", false));
        Assert.Equal(201, made.Status);
        Assert.Equal(400, (await api.Call("POST", "/api/content", R, Item("Reviewer has no batch", true))).Status);
        // A1 posts to batch only (a member's post waits for review; the reviewer approves it)
        var mineOnly = await api.Call("POST", "/api/content", A1, Item("Batch A only", true));
        Assert.Equal(201, mineOnly.Status);
        Assert.Equal(200, (await api.Call("POST", $"/api/review/{(string)mineOnly.Body["id"]!}", R, new { decision = "approve" })).Status);

        string[] Titles(ApiResult r) => r.Body["items"]!.AsArray().Select(i => (string)i!["title"]!).ToArray();
        Assert.Contains("Batch A only", Titles(await api.Call("GET", "/api/feed?topic=evaluation", A2)));          // same batch
        Assert.DoesNotContain("Batch A only", Titles(await api.Call("GET", "/api/feed?topic=evaluation", B1)));    // other batch
        Assert.Contains("Everyone note", Titles(await api.Call("GET", "/api/feed?topic=evaluation", B1)));
        Assert.Contains("Batch A only", Titles(await api.Call("GET", "/api/feed?topic=evaluation", R)));           // reviewers see all
        Assert.Equal(404, (await api.Call("GET", $"/api/content/{(string)mineOnly.Body["id"]!}", B1)).Status);
        Assert.Equal(200, (await api.Call("GET", $"/api/content/{(string)mineOnly.Body["id"]!}", A2)).Status);
    }

    [Fact]
    public async Task C05_the_leaderboard_shows_only_people_who_opted_in_and_only_their_own_batch()
    {
        var empty = (await api.Call("GET", "/api/leaderboard", A2)).Body;
        Assert.False((bool)empty["on"]!); Assert.Empty(empty["rows"]!.AsArray());
        Assert.Equal(204, (await api.Call("PUT", "/api/leaderboard/me", A1, new { on = true })).Status);
        var board = (await api.Call("GET", "/api/leaderboard", A2)).Body;
        Assert.Single(board["rows"]!.AsArray());                                                  // only Asha opted in
        Assert.Equal("Asha", (string)board["rows"]![0]!["name"]!);
        Assert.Equal(1, (int)board["rows"]![0]!["approved"]!);
        Assert.DoesNotContain("@", board.ToJsonString());
        Assert.Empty((await api.Call("GET", "/api/leaderboard", B1)).Body["rows"]!.AsArray());    // another batch sees nothing of batch A
        Assert.Equal(204, (await api.Call("PUT", "/api/leaderboard/me", A1, new { on = false })).Status);
        Assert.Empty((await api.Call("GET", "/api/leaderboard", A2)).Body["rows"]!.AsArray());
    }

    [Fact]
    public async Task C06_invited_people_are_approved_into_their_batch_when_they_first_sign_in()
    {
        var newcomer = new TestUser(ApiFixture.Id(805), "new.person@fiftyfivetech.io");
        Assert.Equal(403, (await api.Call("POST", "/api/admin/invites", R, new { emails = new[] { "x@fiftyfivetech.io" } })).Status);   // admin only
        Assert.Equal(400, (await api.Call("POST", "/api/admin/invites", Admin, new { emails = Array.Empty<string>() })).Status);
        var res = await api.Call("POST", "/api/admin/invites", Admin, new { emails = new[] { "New.Person@fiftyfivetech.io", "outsider@gmail.com", "asha.verma@fiftyfivetech.io" }, batch_id = batchB });
        Assert.Equal(200, res.Status);
        Assert.Equal(1, (int)res.Body["added"]!); Assert.Equal(1, (int)res.Body["existing"]!);
        Assert.Equal("outsider@gmail.com", (string)res.Body["rejected"]![0]!);
        Assert.Equal(1, (int)(await api.Call("GET", "/api/admin/invites", Admin)).Body.AsArray().Count);
        // all free seats are long gone: without the invite this person would be pending
        var me = (await api.Call("GET", "/api/me", newcomer)).Body;
        Assert.Equal("approved", (string)me["access"]!);
        Assert.True((bool)(await api.Call("GET", "/api/admin/invites", Admin)).Body[0]!["used"]!);
        Assert.Equal(1, await api.Count($"select count(*) from app_users where id = '{newcomer.Id}' and batch_id = '{batchB}'"));
    }
}
