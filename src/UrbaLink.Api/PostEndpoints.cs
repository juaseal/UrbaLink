using System.Security.Claims;
using Npgsql;
using UrbaLink.Contracts;

namespace UrbaLink.Api;

public static class PostEndpoints
{
    private static IResult Error(string message, int status) =>
        Results.Json(new ApiError(message), statusCode: status);

    public static void MapPosts(this WebApplication app)
    {
        var routes = app.MapGroup("/api/communities/{communityId:guid}/posts")
            .RequireAuthorization().RequireRateLimiting("api");
        routes.MapGet("", List);
        routes.MapPost("", Create);
        routes.MapDelete("/{postId:long}", Delete);
    }

    private static async Task<IResult> List(Guid communityId, string? category, long? before,
        ClaimsPrincipal principal, NpgsqlDataSource source, CancellationToken ct)
    {
        if ((category is not null && !PostCategories.All.Contains(category) && category != PostCategories.HelpGroup) || before is <= 0)
            return Error("Filtro de publicaciones inválido.", 400);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (await CommunityAccess.Role(connection, communityId, Tokens.UserId(principal), ct) is null)
            return Error("No tienes acceso a esta comunidad.", 403);
        await using var command = Database.Command(connection,
            """
            SELECT p.id,p.community_id,p.author_id,u.name,p.title,p.body,p.category,p.created_at,p.photo
            FROM posts p JOIN users u ON u.id=p.author_id
            WHERE p.community_id=$1 AND p.deleted_at IS NULL
                AND ($2='' OR p.category=$2 OR ($2='Ayuda y servicios' AND p.category IN ('Ayuda','Ofrezco','Recomendación'))) AND p.id < $3
            ORDER BY p.id DESC LIMIT 21
            """, communityId, category ?? "", before ?? long.MaxValue);
        var posts = new List<PostDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) posts.Add(PostRecord.Read(reader));
        var hasMore = posts.Count > 20;
        if (hasMore) posts.RemoveAt(20);
        return Results.Ok(new PostPage(posts.ToArray(), hasMore ? posts[^1].Id : null));
    }

    private static async Task<IResult> Create(Guid communityId, CreatePostRequest request,
        ClaimsPrincipal principal, NpgsqlDataSource source, CancellationToken ct)
    {
        var title = request.Title?.Trim() ?? "";
        var body = request.Body?.Trim() ?? "";
        if (title.Length is < 3 or > 120 || body.Length is < 1 or > 4000 ||
            !PostCategories.All.Contains(request.Category) || (request.Photo is not null && !MarketPhotoRules.IsValid(request.Photo)))
            return Error("Escribe un título de 3–120 caracteres, un mensaje de 1–4000, una categoría válida y, opcionalmente, una foto JPEG de hasta 40 KB.", 400);
        var userId = Tokens.UserId(principal);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (await CommunityAccess.Role(connection, communityId, userId, ct) is null)
            return Error("No tienes acceso a esta comunidad.", 403);
        // Keep permission checks in the insert as well as the transaction-scoped community gate.
        await using var command = Database.Command(connection,
            """
            WITH created AS (
                INSERT INTO posts(community_id,author_id,title,body,category,photo)
                SELECT community_id,user_id,$3,$4,$5,$6 FROM active_memberships
                WHERE community_id=$1 AND user_id=$2 AND ($5 <> 'Aviso oficial' OR role='Admin')
                RETURNING *
            )
            SELECT p.id,p.community_id,p.author_id,u.name,p.title,p.body,p.category,p.created_at,p.photo
            FROM created p JOIN users u ON u.id=p.author_id
            """, communityId, userId, title, body, request.Category, request.Photo);
        PostDto? post;
        await using (var reader = await command.ExecuteReaderAsync(ct))
            post = await reader.ReadAsync(ct) ? PostRecord.Read(reader) : null;
        await transaction.CommitAsync(ct);
        return post is not null ? Results.Ok(post)
            : Error("No tienes permiso para publicar aquí. Los avisos oficiales requieren ser administrador.", 403);
    }

    private static async Task<IResult> Delete(Guid communityId, long postId,
        ClaimsPrincipal principal, NpgsqlDataSource source, CancellationToken ct)
    {
        var userId = Tokens.UserId(principal);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (await CommunityAccess.Role(connection, communityId, userId, ct) is null)
            return Error("No tienes acceso a esta comunidad.", 403);
        await using var command = Database.Command(connection,
            """
            UPDATE posts p SET deleted_at=now(),deleted_by=$3
            FROM active_memberships m
            WHERE p.id=$1 AND p.community_id=$2 AND p.deleted_at IS NULL
                AND m.community_id=p.community_id AND m.user_id=$3
                AND (p.author_id=$3 OR m.role='Admin')
            """, postId, communityId, userId);
        var affected = await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return affected == 1
            ? Results.NoContent()
            : Error("Publicación no disponible o sin permiso para retirarla.", 404);
    }
}

