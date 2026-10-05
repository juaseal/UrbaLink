using System.Net.Mail;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using UrbaLink.Contracts;

namespace UrbaLink.Api;

public static class AccountEndpoints
{
    private static readonly PasswordHasher<string> Hasher = new();
    private static readonly string DummyHash = Hasher.HashPassword("", Tokens.New());
    private static IResult Error(string message, int status = 400) =>
        Results.Json(new ApiError(message), statusCode: status);

    public static void MapAccounts(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireRateLimiting("api");
        api.MapPost("/auth/register", Register).RequireRateLimiting("auth");
        api.MapPost("/auth/login", Login).RequireRateLimiting("auth");
        var secured = api.MapGroup("").RequireAuthorization();
        secured.MapGet("/me", Me);
        secured.MapPost("/auth/logout", Logout);
        secured.MapGet("/communities", Communities);
        secured.MapPost("/communities", CreateCommunity);
        secured.MapPost("/communities/join", Join).RequireRateLimiting("auth");
        secured.MapPost("/communities/{id:guid}/invitations", Invite);
    }

    private static async Task<SessionDto> NewSession(NpgsqlConnection connection, AccountDto account)
    {
        var token = Tokens.New();
        var expiry = DateTimeOffset.UtcNow.AddDays(7);
        await using var command = Database.Command(connection,
            "INSERT INTO sessions(token_hash,user_id,expires_at) VALUES ($1,$2,$3)",
            Tokens.Hash(token), account.Id, expiry);
        await command.ExecuteNonQueryAsync();
        return new SessionDto(token, expiry, account);
    }

    private static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    private static async Task<IResult> Register(RegisterRequest request, NpgsqlDataSource source)
    {
        var name = request.Name?.Trim() ?? "";
        var email = NormalizeEmail(request.Email);
        if (name.Length is < 2 or > 80 || email.Length > 254 ||
            !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email ||
            request.Password is null || request.Password.Length is < 12 or > 128)
            return Error("Indica un nombre de 2–80 caracteres, un correo válido y una contraseña de 12–128 caracteres.");

        var account = new AccountDto(Guid.NewGuid(), name, email);
        var hash = Hasher.HashPassword(email, request.Password);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using var command = Database.Command(connection,
                "INSERT INTO users(id,name,email,password_hash) VALUES ($1,$2,$3,$4)",
                account.Id, name, email, hash);
            await command.ExecuteNonQueryAsync();
            var session = await NewSession(connection, account);
            await transaction.CommitAsync();
            return Results.Ok(session);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Error("No se pudo crear la cuenta. Prueba a iniciar sesión.", 409);
        }
    }

    private static async Task<IResult> Login(LoginRequest request, NpgsqlDataSource source)
    {
        var email = NormalizeEmail(request.Email);
        if (email.Length > 254 || request.Password is null || request.Password.Length is < 1 or > 128)
            return Error("Correo o contraseña incorrectos.", 401);

        await using var connection = await source.OpenConnectionAsync();
        AccountDto? account = null;
        var hash = DummyHash;
        await using (var command = Database.Command(connection,
            "SELECT id,name,email,password_hash FROM users WHERE email=$1", email))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                account = new AccountDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2));
                hash = reader.GetString(3);
            }
        }
        var verified = Hasher.VerifyHashedPassword(email, hash, request.Password);
        if (account is null || verified == PasswordVerificationResult.Failed)
            return Error("Correo o contraseña incorrectos.", 401);
        if (verified == PasswordVerificationResult.SuccessRehashNeeded)
        {
            await using var update = Database.Command(connection,
                "UPDATE users SET password_hash=$1 WHERE id=$2",
                Hasher.HashPassword(email, request.Password), account.Id);
            await update.ExecuteNonQueryAsync();
        }
        return Results.Ok(await NewSession(connection, account));
    }

    private static async Task<IResult> Me(ClaimsPrincipal principal, NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = Database.Command(connection,
            "SELECT id,name,email FROM users WHERE id=$1", Tokens.UserId(principal));
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? Results.Ok(new AccountDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)))
            : Results.Unauthorized();
    }

    private static async Task<IResult> Logout(HttpContext context, NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = Database.Command(connection,
            "DELETE FROM sessions WHERE token_hash=$1", Tokens.Hash(context.Request.Headers.Authorization.ToString()[7..]));
        await command.ExecuteNonQueryAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> Communities(ClaimsPrincipal principal, NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = Database.Command(connection,
            """
            SELECT c.id,c.name,m.role FROM communities c
            JOIN active_memberships m ON m.community_id=c.id WHERE m.user_id=$1 ORDER BY c.name,c.id
            """, Tokens.UserId(principal));
        var result = new List<CommunityDto>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new CommunityDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        return Results.Ok(result);
    }

    private static async Task<IResult> CreateCommunity(CreateCommunityRequest request,
        ClaimsPrincipal principal, NpgsqlDataSource source)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 2 or > 100) return Error("El nombre debe tener entre 2 y 100 caracteres.");
        var id = Guid.NewGuid();
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = Database.Command(connection,
            "INSERT INTO communities(id,name) VALUES ($1,$2)", id, name))
            await command.ExecuteNonQueryAsync();
        await using (var command = Database.Command(connection,
            "INSERT INTO memberships(user_id,community_id,role) VALUES ($1,$2,'Admin')", Tokens.UserId(principal), id))
            await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return Results.Ok(new CommunityDto(id, name, "Admin"));
    }

    private static async Task<IResult> Invite(Guid id, ClaimsPrincipal principal, NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        var code = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var expiry = DateTimeOffset.UtcNow.AddDays(7);
        await using var command = Database.Command(connection,
            """
            INSERT INTO invitations(code_hash,community_id,created_by,expires_at)
            SELECT $1,community_id,user_id,$2 FROM active_memberships
            WHERE community_id=$3 AND user_id=$4 AND role='Admin'
            """, Tokens.Hash(code), expiry, id, Tokens.UserId(principal));
        return await command.ExecuteNonQueryAsync() == 1
            ? Results.Ok(new InvitationDto(code, expiry))
            : Error("No tienes permiso para generar invitaciones.", 403);
    }

    private static async Task<IResult> Join(JoinRequest request, ClaimsPrincipal principal, NpgsqlDataSource source)
    {
        var code = (request.Code ?? "").Trim().ToUpperInvariant();
        if (code.Length != 32 || !code.All(Uri.IsHexDigit))
            return Error("Invitación inválida, caducada o ya utilizada.");
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var hash = Tokens.Hash(code);
        Guid communityId;
        string name;
        await using (var command = Database.Command(connection,
            """
            SELECT i.community_id,c.name FROM invitations i
            JOIN communities c ON c.id=i.community_id
            WHERE c.deleted_at IS NULL AND i.code_hash=$1 AND i.expires_at > now() AND i.used_by IS NULL
            FOR UPDATE OF i
            """, hash))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) return Error("Invitación inválida, caducada o ya utilizada.");
            communityId = reader.GetGuid(0);
            name = reader.GetString(1);
        }
        var userId = Tokens.UserId(principal);
        await using (var command = Database.Command(connection,
            """
            INSERT INTO memberships(user_id,community_id,role) VALUES ($1,$2,'Member')
            ON CONFLICT DO NOTHING
            """, userId, communityId))
        {
            if (await command.ExecuteNonQueryAsync() == 0)
                return Error("Ya perteneces a esta comunidad.", 409);
        }
        await using (var command = Database.Command(connection,
            "UPDATE invitations SET used_by=$1,used_at=now() WHERE code_hash=$2", userId, hash))
            await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return Results.Ok(new CommunityDto(communityId, name, "Member"));
    }
}

