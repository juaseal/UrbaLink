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
        secured.MapPut("/me", UpdateProfile);
        secured.MapPost("/auth/logout", Logout);

    }

    private static async Task<SessionDto> NewSession(NpgsqlConnection connection, AccountDto account, CancellationToken ct)
    {
        var token = Tokens.New();
        var expiry = DateTimeOffset.UtcNow.Add(SessionPolicy.Lifetime);
        await using var command = Database.Command(connection,
            "INSERT INTO sessions(token_hash,user_id,expires_at) VALUES ($1,$2,$3)",
            Tokens.Hash(token), account.Id, expiry);
        await command.ExecuteNonQueryAsync(ct);
        return new SessionDto(token, expiry, account);
    }

    internal static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    internal static async Task<IResult> Register(RegisterRequest request, NpgsqlDataSource source, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        var email = NormalizeEmail(request.Email);
        if (name.Length is < 1 or > 80 || !ValidEmail(email) ||
            request.Password is null || request.Password.Length is < 1 or > 128)
            return Error("Indica tu nombre, un correo válido y una contraseña de 1–128 caracteres.");

        var account = new AccountDto(Guid.NewGuid(), name, email);
        var hash = Hasher.HashPassword(email, request.Password);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using var command = Database.Command(connection,
                "INSERT INTO users(id,name,email,password_hash) VALUES ($1,$2,$3,$4)",
                account.Id, name, email, hash);
            await command.ExecuteNonQueryAsync(ct);
            var session = await NewSession(connection, account, ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(session);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Error("Ese correo ya está asociado a otra cuenta.", 409);
        }
    }

    internal static async Task<IResult> Login(LoginRequest request, NpgsqlDataSource source, CancellationToken ct)
    {
        var identifier = NormalizeEmail(request.Email);
        if (!ValidEmail(identifier) || request.Password is null || request.Password.Length is < 1 or > 128)
            return Error("Correo o contraseña incorrectos.", 401);

        await using var connection = await source.OpenConnectionAsync(ct);
        AccountDto? account = null;
        var hash = DummyHash;
        await using (var command = Database.Command(connection,
            "SELECT id,name,email,password_hash FROM users WHERE email=$1 AND NOT is_suspended", identifier))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                account = new AccountDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2));
                hash = reader.GetString(3);
            }
        }
        var verified = Hasher.VerifyHashedPassword(identifier, hash, request.Password);
        if (account is null || verified == PasswordVerificationResult.Failed)
            return Error("Correo o contraseña incorrectos.", 401);
        if (verified == PasswordVerificationResult.SuccessRehashNeeded)
        {
            await using var update = Database.Command(connection,
                "UPDATE users SET password_hash=$1 WHERE id=$2",
                Hasher.HashPassword(identifier, request.Password), account.Id);
            await update.ExecuteNonQueryAsync(ct);
        }
        return Results.Ok(await NewSession(connection, account, ct));
    }

    private static async Task<IResult> Me(ClaimsPrincipal principal, NpgsqlDataSource source, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = Database.Command(connection,
            "SELECT id,name,email FROM users WHERE id=$1", Tokens.UserId(principal));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? Results.Ok(new AccountDto(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2)?"":reader.GetString(2)))
            : Results.Unauthorized();
    }

    internal static bool ValidEmail(string email) => email.Length is >=1 and <=254 &&
        MailAddress.TryCreate(email,out var parsed) && parsed.Address==email;

    private static async Task<IResult> UpdateProfile(UpdateProfileRequest request, ClaimsPrincipal principal, NpgsqlDataSource source, CancellationToken ct)
    {
        var email=NormalizeEmail(request.Email);
        if(!ValidEmail(email))return Error("El correo es obligatorio. Indica una dirección válida.");
        await using var connection=await source.OpenConnectionAsync(ct);
        try
        {
            await using var command=Database.Command(connection,"UPDATE users SET email=$1 WHERE id=$2 RETURNING id,name,email",email,Tokens.UserId(principal));
            await using var reader=await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct)
                ? Results.Ok(new AccountDto(reader.GetGuid(0),reader.GetString(1),reader.GetString(2)))
                : Results.Unauthorized();
        }
        catch(PostgresException e) when(e.SqlState==PostgresErrorCodes.UniqueViolation)
        {return Error("Ese correo ya está asociado a otra cuenta.",409);}
    }

    private static async Task<IResult> Logout(HttpContext context, NpgsqlDataSource source, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = Database.Command(connection,
            "DELETE FROM sessions WHERE token_hash=$1", Tokens.Hash(WebSessions.RequestToken(context)!));
        await command.ExecuteNonQueryAsync(ct);
        WebSessions.Clear(context);
        return Results.NoContent();
    }

}
