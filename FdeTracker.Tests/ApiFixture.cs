using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FdeTracker.Api.Services.Llm;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace FdeTracker.Tests;

public sealed record TestUser(Guid Id, string Email);

/// <summary>A scripted model: each test sets what it answers and can read what it was sent.</summary>
public sealed class FakeLlm : ILlmClient
{
    public bool Configured { get; set; } = true;
    public bool IsConfigured => Configured;
    public string? Model => "fake-model";
    public Func<string, string, LlmResult>? Handler { get; set; }
    public string LastSystem { get; private set; } = "";
    public string LastUser { get; private set; } = "";
    public int Calls { get; private set; }

    public Task<LlmResult> CompleteAsync(string system, string user, CancellationToken ct)
    {
        LastSystem = system; LastUser = user; Calls++;
        return Task.FromResult((Handler ?? throw new InvalidOperationException("No fake answer set")).Invoke(system, user));
    }
}

public sealed record ApiResult(int Status, JsonNode? Json)
{
    public JsonNode Body => Json ?? throw new InvalidOperationException("No JSON body");
}

/// <summary>Starts a real Postgres in Docker and the API against it, once for the whole test class.</summary>
public sealed class ApiFixture : IAsyncLifetime
{
    const string Secret = "test-secret-test-secret-test-secret-123456";
    const string Issuer = "https://test.supabase.co/auth/v1";
    readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    WebApplicationFactory<Program> _factory = null!;
    HttpClient _client = null!;

    public string ConnectionString => _pg.GetConnectionString();

    public static Guid Id(int n) => Guid.Parse($"00000000-0000-4000-8000-{n:D12}");

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        Environment.SetEnvironmentVariable("DATABASE_URL", _pg.GetConnectionString());
        Environment.SetEnvironmentVariable("SUPABASE_URL", "https://test.supabase.co");
        Environment.SetEnvironmentVariable("SUPABASE_JWT_SECRET", Secret);
        Environment.SetEnvironmentVariable("ADMIN_EMAIL", "boss@fiftyfivetech.io");
        Environment.SetEnvironmentVariable("ALLOWED_DOMAIN", "fiftyfivetech.io");
        Environment.SetEnvironmentVariable("FREE_SEATS", "3");
        Environment.SetEnvironmentVariable("CORS_ORIGINS", "https://app.example");
        Environment.SetEnvironmentVariable("LLM_DAILY_CAP", "20");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<ILlmClient>(); // tests never call a real model
            s.AddSingleton<ILlmClient>(Llm);
        }));
        _client = _factory.CreateClient();
    }

    public FakeLlm Llm { get; } = new();

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _pg.DisposeAsync();
    }

    public static string Token(TestUser u)
    {
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(Issuer, "authenticated", [new Claim("sub", u.Id.ToString()), new Claim("email", u.Email)], expires: DateTime.UtcNow.AddHours(1), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    public async Task<ApiResult> Call(string method, string path, TestUser? user = null, object? body = null, string? rawToken = null)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (user is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(user));
        else if (rawToken is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);
        if (body is not null) req.Content = new StringContent(body is string s ? s : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await _client.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        JsonNode? json = null;
        try { json = string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text); } catch (JsonException) { /* not json */ }
        return new ApiResult((int)res.StatusCode, json);
    }

    public Task<HttpResponseMessage> Raw(string path) => _client.GetAsync(path);

    public Task<HttpResponseMessage> GetFromOrigin(string path, string origin)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("Origin", origin);
        return _client.SendAsync(req);
    }

    public async Task<long> Count(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}

/// <summary>The scenarios share one database and build on each other, so they run in name order (T01, T02, ...).</summary>
public sealed class NameOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases) where TTestCase : ITestCase =>
        testCases.OrderBy(t => t.TestMethod.Method.Name, StringComparer.Ordinal);
}
