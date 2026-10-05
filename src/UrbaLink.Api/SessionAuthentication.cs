using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Npgsql;

namespace UrbaLink.Api;

public static class Tokens
{
    public static string New() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static Guid UserId(ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

public sealed class SessionAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IServiceProvider services) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var token = header[7..];
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) return AuthenticateResult.Fail("Sesión inválida.");
        var source = services.GetService<NpgsqlDataSource>();
        if (source is null) return AuthenticateResult.Fail("Base de datos no disponible.");
        await using var connection = await source.OpenConnectionAsync(Context.RequestAborted);
        await using var command = Database.Command(connection,
            "SELECT user_id FROM sessions WHERE token_hash=$1 AND expires_at > now()", Tokens.Hash(token));
        var id = await command.ExecuteScalarAsync(Context.RequestAborted);
        if (id is not Guid userId) return AuthenticateResult.Fail("Sesión caducada.");
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
