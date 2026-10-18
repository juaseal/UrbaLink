using System.Security.Claims;
using Npgsql;
using UrbaLink.Contracts;

namespace UrbaLink.Api;

public static partial class CommunityEndpoints
{
    private static async Task<IResult> Communities(ClaimsPrincipal principal, NpgsqlDataSource source, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = Database.Command(connection,
            """
            SELECT c.id,c.name,m.role,c.platform_state FROM communities c
            JOIN memberships m ON m.community_id=c.id WHERE m.user_id=$1 AND NOT m.is_blocked AND c.deleted_at IS NULL ORDER BY c.name,c.id
            """, Tokens.UserId(principal));
        var result = new List<CommunityDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new CommunityDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),reader.GetString(3)));
        return Results.Ok(result);
    }

    private static async Task<IResult> CreateCommunity(CreateCommunityRequest request,
        ClaimsPrincipal principal, NpgsqlDataSource source, IConfiguration configuration, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 2 or > 100) return Error("El nombre debe tener entre 2 y 100 caracteres.");
        var id = Guid.NewGuid();
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var command = Database.Command(connection,
            "INSERT INTO communities(id,name,platform_state) VALUES ($1,$2,$3)", id, name, configuration.GetValue("Communities:RequireApproval",true)?"Pending":"Active"))
            await command.ExecuteNonQueryAsync(ct);
        await using (var command = Database.Command(connection,
            "INSERT INTO memberships(user_id,community_id,role) VALUES ($1,$2,'Admin')", Tokens.UserId(principal), id))
            await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new CommunityDto(id, name, "Admin", configuration.GetValue("Communities:RequireApproval",true)?"Pending":"Active"));
    }

    private static async Task<IResult> Invite(Guid id, ClaimsPrincipal principal, NpgsqlDataSource source, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (await CommunityAccess.Role(connection, id, Tokens.UserId(principal), ct) != "Admin")
            return Error("No tienes permiso para generar invitaciones.", 403);
        var code = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var expiry = DateTimeOffset.UtcNow.AddDays(7);
        await using var command = Database.Command(connection,
            """
            INSERT INTO invitations(code_hash,community_id,created_by,expires_at)
            SELECT $1,community_id,user_id,$2 FROM active_memberships
            WHERE community_id=$3 AND user_id=$4 AND role='Admin'
            """, Tokens.Hash(code), expiry, id, Tokens.UserId(principal));
        var inserted = await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return inserted == 1
            ? Results.Ok(new InvitationDto(code, expiry))
            : Error("No tienes permiso para generar invitaciones.", 403);
    }

    private static async Task<IResult> Join(JoinRequest request, ClaimsPrincipal principal, NpgsqlDataSource source, CancellationToken ct)
    {
        var code = (request.Code ?? "").Trim().ToUpperInvariant();
        if (code.Length != 32 || !code.All(Uri.IsHexDigit))
            return Error("Invitación inválida, caducada o ya utilizada.");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var hash = Tokens.Hash(code);
        var target = await Database.Scalar(connection, ct, "SELECT community_id FROM invitations WHERE code_hash=$1", hash);
        if (target is not Guid targetId || await CommunityAccess.LockActive(connection, targetId, ct) is null)
            return Error("Invitación inválida, caducada o ya utilizada.");
        Guid communityId;
        string name;
        await using (var command = Database.Command(connection,
            """
            SELECT i.community_id,c.name FROM invitations i
            JOIN communities c ON c.id=i.community_id
            WHERE c.deleted_at IS NULL AND i.code_hash=$1 AND i.expires_at > clock_timestamp() AND i.used_by IS NULL
            FOR UPDATE OF i
            """, hash))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return Error("Invitación inválida, caducada o ya utilizada.");
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
            if (await command.ExecuteNonQueryAsync(ct) == 0)
                return Error("Ya perteneces a esta comunidad.", 409);
        }
        await using (var command = Database.Command(connection,
            "UPDATE invitations SET used_by=$1,used_at=now() WHERE code_hash=$2", userId, hash))
            await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new CommunityDto(communityId, name, "Member"));
    }
}
