namespace FdeTracker.Api.Common;

/// <summary>Settings read from environment variables (set in the Render dashboard in production).</summary>
public sealed class AppOptions
{
    public string DatabaseUrl { get; init; } = "";
    public string SupabaseUrl { get; init; } = "";
    public string? SupabaseJwtSecret { get; init; }
    public string AdminEmail { get; init; } = "shivanshu.rai@fiftyfivetech.io";
    public string AllowedDomain { get; init; } = "fiftyfivetech.io";
    public int FreeSeats { get; init; } = 3;
    public string[] CorsOrigins { get; init; } = [];

    // The assistant is optional: without all three LLM_* values it answers 503 and nothing else is affected.
    public string? LlmBaseUrl { get; init; }
    public string? LlmApiKey { get; init; }
    public string? LlmModel { get; init; }
    public string LlmProvider { get; init; } = "openai"; // "openai" = any chat-completions provider, "anthropic" = Claude
    public int LlmDailyCap { get; init; } = 20;
    public int LlmTimeoutSeconds { get; init; } = 30;

    static string ProviderOf(string? value)
    {
        var p = (value ?? "openai").ToLowerInvariant();
        return p is "openai" or "anthropic" ? p : throw new InvalidOperationException("LLM_PROVIDER must be openai or anthropic");
    }

    public static AppOptions From(IConfiguration c)
    {
        string Need(string key) => c[key] is { Length: > 0 } v ? v : throw new InvalidOperationException($"Missing required setting {key}");
        string? Opt(string key) => c[key] is { Length: > 0 } v ? v : null;
        int Num(string key, int fallback) => int.TryParse(c[key], out var n) && n > 0 ? n : fallback;
        return new AppOptions
        {
            DatabaseUrl = Need("DATABASE_URL"),
            SupabaseUrl = Need("SUPABASE_URL"),
            SupabaseJwtSecret = Opt("SUPABASE_JWT_SECRET"),
            AdminEmail = (c["ADMIN_EMAIL"] ?? "shivanshu.rai@fiftyfivetech.io").ToLowerInvariant(),
            AllowedDomain = (c["ALLOWED_DOMAIN"] ?? "fiftyfivetech.io").ToLowerInvariant(),
            FreeSeats = int.TryParse(c["FREE_SEATS"], out var n) ? n : 3,
            CorsOrigins = (c["CORS_ORIGINS"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            LlmBaseUrl = Opt("LLM_BASE_URL"),
            LlmApiKey = Opt("LLM_API_KEY"),
            LlmModel = Opt("LLM_MODEL"),
            LlmProvider = ProviderOf(Opt("LLM_PROVIDER")),
            LlmDailyCap = Num("LLM_DAILY_CAP", 20),
            LlmTimeoutSeconds = Num("LLM_TIMEOUT_SECONDS", 30),
        };
    }
}
