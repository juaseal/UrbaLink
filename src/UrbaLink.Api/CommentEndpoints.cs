using System.Security.Claims;
using Npgsql;
using UrbaLink.Contracts;

namespace UrbaLink.Api;

public static class CommentEndpoints
{
    private static IResult Error(string message, int status = 404) =>
        Results.Json(new ApiError(message), statusCode: status);

    public static void MapComments(this WebApplication app)
    {
        var routes = app.MapGroup("/api/communities/{communityId:guid}/posts/{postId:long}/comments")
            .RequireAuthorization().RequireRateLimiting("api");
        routes.MapGet("", List);
        routes.MapPost("", Create);
        routes.MapDelete("/{commentId:long}", Delete);
    }

    // Scope both the post and membership; never expose a post from another community.
    // The share lock prevents a post being withdrawn during a comment mutation.
    private static async Task<PostDto?> FindPost(NpgsqlConnection connection, Guid communityId,
        long postId, Guid userId, CancellationToken ct)
    {
        await using var command = Database.Command(connection,
            """
            SELECT p.id,p.community_id,p.author_id,u.name,p.title,p.body,p.category,p.created_at
            FROM posts p JOIN users u ON u.id=p.author_id
            JOIN active_memberships m ON m.community_id=p.community_id AND m.user_id=$3
            WHERE p.community_id=$1 AND p.id=$2 AND p.deleted_at IS NULL AND p.category<>'Ofrezco'
            FOR SHARE OF p
            """, communityId, postId, userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new PostDto(
            reader.GetInt64(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6),
            new DateTimeOffset(reader.GetDateTime(7))) : null;
    }

    private static CommentDto Read(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), reader.GetGuid(2), reader.GetString(3),
        reader.GetString(4), new DateTimeOffset(reader.GetDateTime(5)));

    private static async Task<IResult> List(Guid communityId, long postId, long? before,
        ClaimsPrincipal principal, NpgsqlDataSource source, CancellationToken ct)
    {
        if (before is <= 0) return Error("Cursor de comentarios inválido.", 400);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var post = await FindPost(connection, communityId, postId, Tokens.UserId(principal), ct);
        if (post is null) return Error("La publicación no está disponible o no tienes acceso.");
        var comments = new List<CommentDto>();
        await using (var command = Database.Command(connection,
            """
            SELECT c.id,c.post_id,c.author_id,u.name,c.body,c.created_at
            FROM comments c JOIN users u ON u.id=c.author_id
            WHERE c.post_id=$1 AND c.deleted_at IS NULL AND c.id<$2
            ORDER BY c.id DESC LIMIT 21
            """, postId, before ?? long.MaxValue))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) comments.Add(Read(reader));
        await transaction.CommitAsync(ct);
        var hasMore = comments.Count > 20;
        if (hasMore) comments.RemoveAt(20);
        return Results.Ok(new CommentPage(post, comments.ToArray(), hasMore ? comments[^1].Id : null));
    }

    private static async Task<IResult> Create(Guid communityId, long postId, CreateCommentRequest request,
        ClaimsPrincipal principal, NpgsqlDataSource source, CancellationToken ct)
    {
        var body = request.Body?.Trim() ?? "";
        if (body.Length is < 1 or > 2000 || request.RequestId == Guid.Empty)
            return Error("Escribe un comentario de 1–2000 caracteres con un identificador de envío válido.", 400);
        var userId = Tokens.UserId(principal);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (await FindPost(connection, communityId, postId, userId, ct) is null)
            return Error("La publicación no está disponible o no tienes acceso.");
        await using (var command = Database.Command(connection,
            """
            INSERT INTO comments(post_id,author_id,body,request_id) VALUES ($1,$2,$3,$4)
            ON CONFLICT (post_id,author_id,request_id) DO NOTHING
            """, postId, userId, body, request.RequestId))
            await command.ExecuteNonQueryAsync(ct);

        CommentDto? result = null;
        await using (var command = Database.Command(connection,
            """
            SELECT c.id,c.post_id,c.author_id,u.name,c.body,c.created_at
            FROM comments c JOIN users u ON u.id=c.author_id
            WHERE c.post_id=$1 AND c.author_id=$2 AND c.request_id=$3 AND c.deleted_at IS NULL
            """, postId, userId, request.RequestId))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct)) result = Read(reader);
        if (result is null || result.Body != body)
            return Error("Este envío ya se utilizó para otro texto o un comentario retirado. Actualiza antes de volver a comentar.", 409);
        await transaction.CommitAsync(ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> Delete(Guid communityId, long postId, long commentId,
        ClaimsPrincipal principal, NpgsqlDataSource source, CancellationToken ct)
    {
        var userId = Tokens.UserId(principal);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (await FindPost(connection, communityId, postId, userId, ct) is null)
            return Error("La publicación no está disponible o no tienes acceso.");
        await using var command = Database.Command(connection,
            """
            UPDATE comments c SET deleted_at=now(),deleted_by=$3
            FROM active_memberships m
            WHERE c.id=$1 AND c.post_id=$2 AND c.deleted_at IS NULL
                AND m.community_id=$4 AND m.user_id=$3
                AND (c.author_id=$3 OR m.role='Admin')
            """, commentId, postId, userId, communityId);
        var count = await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return count == 1 ? Results.NoContent()
            : Error("Comentario no disponible o sin permiso para retirarlo.");
    }
}


