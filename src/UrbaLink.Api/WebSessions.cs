using Npgsql;
using UrbaLink.Contracts;

namespace UrbaLink.Api;

public static class WebSessions
{
    internal const string CookieName = "UrbaLink.WebSession";

    internal static bool IsSameOriginRequest(HttpContext context)
    {
        if (context.Request.Headers["X-UrbaLink-Web"] != "1") return false;
        var origin = context.Request.Headers.Origin.ToString();
        return origin.Length == 0 || string.Equals(origin,
            $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? RequestToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return header[7..];
        // Never fall back to cookies when an explicit, unsupported credential is supplied.
        return header.Length == 0 ? context.Request.Cookies[CookieName] : null;
    }

    private static CookieOptions Options(HttpContext context) => new()
    {
        HttpOnly = true,
        Secure = !context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment() || context.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = "/api",
        IsEssential = true
    };

    internal static void Set(HttpContext context, string token, DateTimeOffset expires)
    {
        var options = Options(context); options.Expires = expires;
        context.Response.Cookies.Append(CookieName, token, options);
    }

    internal static void Clear(HttpContext context) => context.Response.Cookies.Delete(CookieName, Options(context));

    public static void MapWebSessions(this WebApplication app)
    {
        var group = app.MapGroup("/api/web").RequireRateLimiting("auth");
        group.AddEndpointFilter(async (context, next) => IsSameOriginRequest(context.HttpContext)
            ? await next(context)
            : Results.Json(new ApiError("Solicitud no válida. Abre UrbaLink desde su propia web."), statusCode: 403));
        group.MapPost("/session", async (LoginRequest request, HttpContext context, NpgsqlDataSource source, CancellationToken ct) =>
            await Complete(await AccountEndpoints.Login(request, source, ct), context, source, ct));
        group.MapPost("/register", async (RegisterRequest request, HttpContext context, NpgsqlDataSource source, CancellationToken ct) =>
            await Complete(await AccountEndpoints.Register(request, source, ct), context, source, ct));
    }

    private static async Task<IResult> Complete(IResult result, HttpContext context, NpgsqlDataSource source, CancellationToken ct)
    {
        if (result is not IValueHttpResult { Value: SessionDto session }) return result;
        if (context.Request.Cookies[CookieName] is { Length: 64 } old)
        {
            await using var connection = await source.OpenConnectionAsync(ct);
            await Database.Execute(connection, ct, "DELETE FROM sessions WHERE token_hash=$1", Tokens.Hash(old));
        }
        Set(context, session.Token, session.ExpiresAt);
        return Results.Ok(session.Account);
    }
}
