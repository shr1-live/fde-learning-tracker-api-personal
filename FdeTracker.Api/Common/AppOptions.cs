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

    public static AppOptions From(IConfiguration c)
    {
        string Need(string key) => c[key] is { Length: > 0 } v ? v : throw new InvalidOperationException($"Missing required setting {key}");
        return new AppOptions
        {
            DatabaseUrl = Need("DATABASE_URL"),
            SupabaseUrl = Need("SUPABASE_URL"),
            SupabaseJwtSecret = c["SUPABASE_JWT_SECRET"] is { Length: > 0 } s ? s : null,
            AdminEmail = (c["ADMIN_EMAIL"] ?? "shivanshu.rai@fiftyfivetech.io").ToLowerInvariant(),
            AllowedDomain = (c["ALLOWED_DOMAIN"] ?? "fiftyfivetech.io").ToLowerInvariant(),
            FreeSeats = int.TryParse(c["FREE_SEATS"], out var n) ? n : 3,
            CorsOrigins = (c["CORS_ORIGINS"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };
    }
}
