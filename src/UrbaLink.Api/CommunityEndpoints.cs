using System.Security.Claims;
using Npgsql;
using UrbaLink.Contracts;

namespace UrbaLink.Api;

public static partial class CommunityEndpoints
{
    private static IResult Error(string text, int status = 400) => Results.Json(new ApiError(text), statusCode: status);

    public static void MapCommunityManagement(this WebApplication app)
    {
        var group = app.MapGroup("/api/communities").RequireAuthorization().RequireRateLimiting("api");
        group.MapGet("", Communities);
        group.MapPost("", CreateCommunity);
        group.MapPost("/join", Join).RequireRateLimiting("auth");
        group.MapPost("/{id:guid}/invitations", Invite);
        group.MapGet("/{id:guid}/invitations", Invitations);
        group.MapDelete("/{id:guid}/invitations/{invitationId:guid}", RevokeInvitation);
        group.MapGet("/trash", Trash);
        group.MapGet("/{id:guid}/overview", Overview);
        group.MapPost("/{id:guid}/delete", Delete);
        group.MapPost("/{id:guid}/restore", Restore);
    }

    private static async Task<IResult> Trash(ClaimsPrincipal user, NpgsqlDataSource source, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = Database.Command(connection,
            """
            SELECT c.id,c.name,m.role FROM communities c JOIN memberships m ON m.community_id=c.id
            WHERE c.deleted_at IS NOT NULL AND m.user_id=$1 AND m.role='Admin' ORDER BY c.deleted_at DESC,c.id
            """, Tokens.UserId(user));
        var result = new List<CommunityDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2)));
        return Results.Ok(result);
    }

    private static async Task<IResult> Delete(Guid id, DeleteCommunityRequest request, ClaimsPrincipal user, NpgsqlDataSource source, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = Database.Command(connection,
            """
            UPDATE communities c SET deleted_at=now(),deleted_by=$2 FROM memberships m
            WHERE c.id=$1 AND c.deleted_at IS NULL AND c.name=$3
                AND m.community_id=c.id AND m.user_id=$2 AND m.role='Admin'
            """, id, Tokens.UserId(user), request.ConfirmName ?? "");
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            return Error("Comprueba el nombre exacto y tus permisos de administrador.", 409);
        await using var expire = Database.Command(connection,
            "UPDATE invitations SET expires_at=LEAST(expires_at,now()) WHERE community_id=$1", id);
        await expire.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Restore(Guid id, ClaimsPrincipal user, NpgsqlDataSource source, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = Database.Command(connection,
            """
            UPDATE communities c SET deleted_at=NULL,deleted_by=NULL FROM memberships m
            WHERE c.id=$1 AND c.deleted_at IS NOT NULL AND m.community_id=c.id AND m.user_id=$2 AND m.role='Admin'
            """, id, Tokens.UserId(user));
        if (await command.ExecuteNonQueryAsync(ct) != 1) return Error("Comunidad no disponible o sin permisos.", 404);
        await using var expire = Database.Command(connection,
            "UPDATE invitations SET expires_at=LEAST(expires_at,now()) WHERE community_id=$1", id);
        await expire.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Overview(Guid id, ClaimsPrincipal user, NpgsqlDataSource source, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (await CommunityAccess.Role(connection, id, Tokens.UserId(user), ct) is null)
            return Error("No tienes acceso a esta comunidad.", 403);
        await using var command = Database.Command(connection,
            """
            SELECT c.name,m.role,
                (SELECT count(*)::int FROM active_memberships WHERE community_id=c.id),
                (SELECT count(*)::int FROM posts WHERE community_id=c.id AND deleted_at IS NULL),
                (SELECT count(*)::int FROM market_items WHERE community_id=c.id AND state='Active' AND (mode<>'Auction' OR ends_at>clock_timestamp())),
                (SELECT count(*)::int FROM posts WHERE community_id=c.id AND deleted_at IS NULL AND category='Coche')
            FROM communities c JOIN active_memberships m ON m.community_id=c.id
            WHERE c.id=$1 AND m.user_id=$2
            """, id, Tokens.UserId(user));
        CommunityDto community;
        int members, total, material, routes;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return Error("No tienes acceso a esta comunidad.", 403);
            community = new(id,reader.GetString(0),reader.GetString(1));
            members=reader.GetInt32(2); total=reader.GetInt32(3); material=reader.GetInt32(4); routes=reader.GetInt32(5);
        }
        async Task<PostDto[]> Posts(bool official)
        {
            await using var query = Database.Command(connection,
                """
                SELECT p.id,p.community_id,p.author_id,u.name,p.title,p.body,p.category,p.created_at,p.photo
                FROM posts p JOIN users u ON u.id=p.author_id
                WHERE p.community_id=$1 AND p.deleted_at IS NULL AND ($2=false OR p.category='Aviso oficial')
                ORDER BY p.id DESC LIMIT 3
                """, id, official);
            var list = new List<PostDto>();
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                list.Add(PostRecord.Read(reader));
            return list.ToArray();
        }
        return Results.Ok(new CommunityOverview(community,members,total,material,routes,
            (await Posts(true)).FirstOrDefault(),await Posts(false)));
    }
}
