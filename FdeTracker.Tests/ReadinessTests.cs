using System.Text.Json.Nodes;

namespace FdeTracker.Tests;

/// <summary>Readiness self-ratings across people (reviewers and the admin only). The ratings live in the web app's
/// <c>entries</c> table; the test creates that table the way the web app's setup does. Its own database (own fixture).</summary>
[TestCaseOrderer("FdeTracker.Tests.NameOrderer", "FdeTracker.Tests")]
[Collection(ApiFixture.Collection)]
public sealed class ReadinessTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    static readonly TestUser Admin = new(ApiFixture.Id(1), "boss@fiftyfivetech.io");
    static readonly TestUser M1 = new(ApiFixture.Id(501), "asha.verma@fiftyfivetech.io");
    static readonly TestUser M2 = new(ApiFixture.Id(502), "karan.singh@fiftyfivetech.io");
    static readonly TestUser R = new(ApiFixture.Id(503), "reviewer.r@fiftyfivetech.io");

    async Task Rate(TestUser u, string key, object value) =>
        await api.Count($"with x as (insert into public.entries (user_id, key, \"value\") values ('{u.Id}', '{key}', '{value}'::jsonb) on conflict (user_id, key) do update set \"value\" = excluded.\"value\" returning 1) select count(*) from x");

    [Fact]
    public async Task Q01_nobody_has_rated_yet_means_an_empty_list_even_before_the_entries_table_exists()
    {
        foreach (var u in new[] { Admin, M1, M2, R }) Assert.Equal(200, (await api.Call("GET", "/api/me", u)).Status);
        Assert.Equal(200, (await api.Call("POST", $"/api/admin/users/{R.Id}/role", Admin, new { role = "reviewer" })).Status);
        Assert.Equal(200, (await api.Call("PATCH", "/api/me", M1, new { display_name = "Asha" })).Status);
        var none = await api.Call("GET", "/api/readiness", R);
        Assert.Equal(200, none.Status);
        Assert.Empty(none.Body.AsArray());   // the table is not there yet: nobody has rated
    }

    [Fact]
    public async Task Q02_reviewers_and_the_admin_see_everyones_ratings_members_do_not()
    {
        await api.Count("with x as (select 1) select count(*) from x"); // keep the helper warm
        await Exec("create table if not exists public.entries (user_id uuid not null, key text not null, \"value\" jsonb not null, updated_at timestamptz not null default now(), primary key (user_id, key))");
        await Rate(M1, "rate:base:0", 2); await Rate(M1, "rate:final:0", 4);
        await Rate(M1, "rate:base:3", 1);
        await Rate(M2, "rate:base:0", 3);
        await Rate(M2, "d1-p0", "true");           // not a rating: ignored
        await Rate(M2, "rate:final:1", 9);         // out of range: ignored
        await Rate(M2, "rate:base:x", 3);          // not a skill index: ignored

        Assert.Equal(403, (await api.Call("GET", "/api/readiness", M1)).Status);   // a member cannot read anyone's, not even their own here
        Assert.Equal(401, (await api.Call("GET", "/api/readiness")).Status);
        foreach (var viewer in new[] { R, Admin })
        {
            var list = (await api.Call("GET", "/api/readiness", viewer)).Body.AsArray();
            Assert.Equal(2, list.Count);
            var asha = list.Single(p => (string)p!["name"]! == "Asha")!;
            Assert.Equal(2, (int)asha["base"]!["0"]!);
            Assert.Equal(4, (int)asha["final"]!["0"]!);
            Assert.Equal(1, (int)asha["base"]!["3"]!);
            var karan = list.Single(p => (string)p!["name"]! == "karan.singh")!;   // no display name: the part before "@"
            Assert.Equal(3, (int)karan["base"]!["0"]!);
            Assert.Empty(karan["final"]!.AsObject());
            Assert.DoesNotContain("@", list.ToJsonString());                           // never an email address
        }
    }

    [Fact]
    public async Task Q03_a_batch_filter_narrows_the_list()
    {
        var batch = await api.Call("POST", "/api/admin/batches", Admin, new { name = "Batch Q", starts_on = "2026-01-05" });
        Assert.Equal(201, batch.Status);
        Assert.Equal(200, (await api.Call("POST", $"/api/admin/users/{M1.Id}/batch", Admin, new { batch_id = (string)batch.Body["id"]! })).Status);
        var only = (await api.Call("GET", $"/api/readiness?batch={(string)batch.Body["id"]!}", R)).Body.AsArray();
        Assert.Single(only);
        Assert.Equal("Batch Q", (string)only[0]!["batch_name"]!);
    }

    async Task Exec(string sql) { await using var c = new Npgsql.NpgsqlConnection(api.ConnectionString); await c.OpenAsync(); await using var cmd = new Npgsql.NpgsqlCommand(sql, c); await cmd.ExecuteNonQueryAsync(); }
}
