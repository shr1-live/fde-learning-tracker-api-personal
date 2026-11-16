using System.Security.Claims;
using System.Text.Json;
using FdeTracker.Api.Common;
using FdeTracker.Api.Models;
using FdeTracker.Api.Services;
using Microsoft.AspNetCore.Authorization;

namespace FdeTracker.Api.Middleware;

public static class HttpContextUser
{
    internal const string Key = "fde.user";
    public static AppUser GetUser(this HttpContext ctx) => (AppUser)ctx.Items[Key]!;
}

/// <summary>Writes the standard error body: { "error": "..." }.</summary>
public static class ErrorBody
{
    public static Task Write(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { error = message });
    }
}

public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task Invoke(HttpContext ctx)
    {
        try { await next(ctx); }
        catch (ApiException e) when (!ctx.Response.HasStarted) { await ErrorBody.Write(ctx, e.Status, e.Message); }
        catch (BadHttpRequestException e) when (!ctx.Response.HasStarted)
        {
            await ErrorBody.Write(ctx, e.StatusCode, e.StatusCode == 413 ? "Request too large" : "Invalid request");
        }
        catch (JsonException) when (!ctx.Response.HasStarted) { await ErrorBody.Write(ctx, 400, "Invalid JSON"); }
        catch (Exception e)
        {
            logger.LogError(e, "Unhandled error");
            if (!ctx.Response.HasStarted) await ErrorBody.Write(ctx, 500, "Something went wrong");
        }
    }
}

/// <summary>Adds the usual hardening headers and caps request bodies at 64 KB.</summary>
public sealed class SecurityMiddleware(RequestDelegate next, IHostEnvironment env)
{
    public const long MaxBody = 64 * 1024;

    public Task Invoke(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        if (!env.IsDevelopment()) h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'"; // the dev docs page needs scripts
        h["Strict-Transport-Security"] = "max-age=15552000; includeSubDomains";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        if (ctx.Request.ContentLength > MaxBody) return ErrorBody.Write(ctx, 413, "Request too large");
        if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } f) f.MaxRequestBodySize = MaxBody;
        return next(ctx);
    }
}

/// <summary>After authentication: checks the account is on the allowed domain and loads (or creates) the app user.</summary>
public sealed class CurrentUserMiddleware(RequestDelegate next)
{
    public async Task Invoke(HttpContext ctx, UserService users, AppOptions options)
    {
        var needsUser = ctx.GetEndpoint()?.Metadata.GetMetadata<IAuthorizeData>() is not null && ctx.User.Identity?.IsAuthenticated == true;
        if (!needsUser) { await next(ctx); return; }

        var email = (ctx.User.FindFirstValue("email") ?? "").ToLowerInvariant();
        if (!Guid.TryParseExact(ctx.User.FindFirstValue("sub"), "D", out var id) || !email.EndsWith("@" + options.AllowedDomain, StringComparison.Ordinal))
        {
            await ErrorBody.Write(ctx, 403, "This account is not allowed");
            return;
        }
        ctx.Items[HttpContextUser.Key] = await users.SignIn(id, email);
        await next(ctx);
    }
}
