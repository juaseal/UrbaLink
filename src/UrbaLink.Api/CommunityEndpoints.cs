using System.Security.Claims;
using Npgsql;
using UrbaLink.Contracts;

namespace UrbaLink.Api;

public static class CommunityEndpoints
{
    private static IResult Error(string text, int status) => Results.Json(new ApiError(text), statusCode: status);

    public static void MapCommunityManagement(this WebApplication app)
    {
        var group = app.MapGroup("/api/communities").RequireAuthorization().RequireRateLimiting("api");
        group.MapGet("/trash", Trash);
        group.MapGet("/{id:guid}/overview", Overview);
        group.MapPost("/{id:guid}/delete", Delete);
        group.MapPost("/{id:guid}/restore", Restore);
    }

    private static async Task<IResult> Trash(ClaimsPrincipal user, NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = Database.Command(connection,
            """
            SELECT c.id,c.name,m.role FROM communities c JOIN memberships m ON m.community_id=c.id
            WHERE c.deleted_at IS NOT NULL AND m.user_id=$1 AND m.role='Admin' ORDER BY c.deleted_at DESC,c.id
            """, Tokens.UserId(user));
        var result = new List<CommunityDto>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2)));
        return Results.Ok(result);
    }

    private static async Task<IResult> Delete(Guid id, DeleteCommunityRequest request, ClaimsPrincipal user, NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = Database.Command(connection,
            """
            UPDATE communities c SET deleted_at=now(),deleted_by=$2 FROM memberships m
            WHERE c.id=$1 AND c.deleted_at IS NULL AND c.name=$3
                AND m.community_id=c.id AND m.user_id=$2 AND m.role='Admin'
            """, id, Tokens.UserId(user), request.ConfirmName ?? "");
        if (await command.ExecuteNonQueryAsync() != 1)
            return Error("Comprueba el nombre exacto y tus permisos de administrador.", 409);
        await using var expire = Database.Command(connection,
            "UPDATE invitations SET expires_at=LEAST(expires_at,now()) WHERE community_id=$1", id);
        await expire.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> Restore(Guid id, ClaimsPrincipal user, NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = Database.Command(connection,
            """
            UPDATE communities c SET deleted_at=NULL,deleted_by=NULL FROM memberships m
            WHERE c.id=$1 AND c.deleted_at IS NOT NULL AND m.community_id=c.id AND m.user_id=$2 AND m.role='Admin'
            """, id, Tokens.UserId(user));
        if (await command.ExecuteNonQueryAsync() != 1) return Error("Comunidad no disponible o sin permisos.", 404);
        await using var expire = Database.Command(connection,
            "UPDATE invitations SET expires_at=LEAST(expires_at,now()) WHERE community_id=$1", id);
        await expire.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> Overview(Guid id, ClaimsPrincipal user, NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = Database.Command(connection,
            """
            SELECT c.name,m.role,
                (SELECT count(*)::int FROM active_memberships WHERE community_id=c.id),
                (SELECT count(*)::int FROM posts WHERE community_id=c.id AND deleted_at IS NULL),
                (SELECT count(*)::int FROM posts WHERE community_id=c.id AND deleted_at IS NULL AND category='Material'),
                (SELECT count(*)::int FROM posts WHERE community_id=c.id AND deleted_at IS NULL AND category='Coche')
            FROM communities c JOIN active_memberships m ON m.community_id=c.id
            WHERE c.id=$1 AND m.user_id=$2
            """, id, Tokens.UserId(user));
        CommunityDto community;
        int members, total, material, routes;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) return Error("No tienes acceso a esta comunidad.", 403);
            community = new(id,reader.GetString(0),reader.GetString(1));
            members=reader.GetInt32(2); total=reader.GetInt32(3); material=reader.GetInt32(4); routes=reader.GetInt32(5);
        }
        async Task<PostDto[]> Posts(bool official)
        {
            await using var query = Database.Command(connection,
                """
                SELECT p.id,p.community_id,p.author_id,u.name,p.title,p.body,p.category,p.created_at
                FROM posts p JOIN users u ON u.id=p.author_id
                WHERE p.community_id=$1 AND p.deleted_at IS NULL AND ($2=false OR p.category='Aviso oficial')
                ORDER BY p.id DESC LIMIT 3
                """, id, official);
            var list = new List<PostDto>();
            await using var reader = await query.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(new(reader.GetInt64(0),reader.GetGuid(1),reader.GetGuid(2),reader.GetString(3),
                    reader.GetString(4),reader.GetString(5),reader.GetString(6),new DateTimeOffset(reader.GetDateTime(7))));
            return list.ToArray();
        }
        return Results.Ok(new CommunityOverview(community,members,total,material,routes,
            (await Posts(true)).FirstOrDefault(),await Posts(false)));
    }
}
