using System.ComponentModel.DataAnnotations;

namespace FdeTracker.Api.Models.Requests;

public static class ContentPatterns
{
    public const string Topic = "^[a-z0-9-]{2,40}\\z";
    public const string Stack = "^(dotnet|node|java|python)\\z";
    public const string Kind = "^(article|video|link|note)\\z";
}

public static class ContentRules
{
    /// <summary>Links need a url, text kinds need a body. Returns the field and message, or null when fine.</summary>
    public static (string Field, string Message)? Check(string? kind, string? url, string? body)
    {
        if (kind is "video" or "link" && string.IsNullOrEmpty(url)) return ("url", "A link is required for this kind");
        if (kind is "article" or "note" && string.IsNullOrWhiteSpace(body)) return ("body_md", "Text is required for this kind");
        return null;
    }
}

/// <summary>Links must be absolute https URLs.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class HttpsUrlAttribute : ValidationAttribute
{
    public HttpsUrlAttribute() : base("Links must use https") { }

    protected override ValidationResult? IsValid(object? value, ValidationContext context) =>
        value is null or "" || (value is string s && s.StartsWith("https://", StringComparison.Ordinal) && Uri.TryCreate(s, UriKind.Absolute, out _))
            ? ValidationResult.Success
            : new ValidationResult(ErrorMessage, [context.MemberName ?? "url"]);
}
