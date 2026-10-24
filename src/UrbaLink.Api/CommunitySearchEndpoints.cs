using System.Globalization;
using System.Security.Claims;
using Npgsql;
using UrbaLink.Contracts;

namespace UrbaLink.Api;

public static class CommunitySearchEndpoints
{
    public static void MapCommunitySearch(this WebApplication app) =>
        app.MapGet("/api/communities/{communityId:guid}/search",Search)
            .RequireAuthorization().RequireRateLimiting("api");

    private static async Task<IResult> Search(Guid communityId,string? q,string? before,
        ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)
    {
        var query=q?.Trim()??"";
        var beforeAt=DateTimeOffset.MaxValue;
        var beforeKind="";
        var beforeId=long.MaxValue;
        if(query.Length is <1 or >120)
            return Results.Json(new ApiError("Escribe entre 1 y 120 caracteres para buscar."),statusCode:400);
        if(before is not null)
        {
            var parts=before.Split('|');
            if(parts.Length!=3 || !DateTimeOffset.TryParseExact(parts[0],"O",CultureInfo.InvariantCulture,DateTimeStyles.None,out beforeAt) ||
                parts[1] is not ("Post" or "Item" or "Animal") || !long.TryParse(parts[2],NumberStyles.None,CultureInfo.InvariantCulture,out beforeId) || beforeId<=0)
                return Results.Json(new ApiError("Página de búsqueda no válida."),statusCode:400);
            beforeAt=beforeAt.ToUniversalTime();beforeKind=parts[1];
        }
        await using var c=await source.OpenConnectionAsync(ct);
        await using var tx=await c.BeginTransactionAsync(ct);
        if(await CommunityAccess.Role(c,communityId,Tokens.UserId(user),ct) is null)
            return Results.Json(new ApiError("No tienes acceso a esta comunidad."),statusCode:403);
        await using var command=Database.Command(c,"""
            SELECT x.kind,x.id,x.title,x.description,u.name,x.created_at,x.category
            FROM (
                SELECT 'Post'::text kind,p.id,p.title,p.body description,p.author_id,p.created_at,p.category
                FROM posts p WHERE p.community_id=$1 AND p.deleted_at IS NULL
                UNION ALL
                SELECT 'Item',i.id,i.title,i.description,i.owner_id,i.created_at,'Compraventa'
                FROM market_items i WHERE i.community_id=$1 AND i.state<>'Withdrawn'
                UNION ALL
                SELECT 'Animal',n.id,
                    (CASE n.species WHEN 'Dog' THEN 'Perro' ELSE 'Gato' END)||' '||
                    (CASE n.kind WHEN 'Lost' THEN 'perdido' ELSE 'encontrado' END)||
                    CASE WHEN n.name='' THEN '' ELSE ' · '||n.name END,
                    n.description,n.author_id,n.created_at,'Animales'
                FROM animal_notices n WHERE n.community_id=$1 AND n.status<>'Withdrawn'
            ) x JOIN users u ON u.id=x.author_id
            WHERE strpos(translate(lower(x.title||' '||x.description),'áéíóúü','aeiouu'),
                translate(lower($2),'áéíóúü','aeiouu'))>0
                AND (x.created_at,x.kind,x.id)<($3,$4,$5)
            ORDER BY x.created_at DESC,x.kind DESC,x.id DESC LIMIT 21
            """,communityId,query,beforeAt,beforeKind,beforeId);
        var results=new List<CommunitySearchResult>();
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))results.Add(new(reader.GetString(0),reader.GetInt64(1),reader.GetString(2),reader.GetString(3),
            reader.GetString(4),new DateTimeOffset(reader.GetDateTime(5)),reader.GetString(6)));
        var more=results.Count>20;
        if(more)results.RemoveAt(20);
        var last=results.LastOrDefault();
        var next=more?$"{last!.CreatedAt:O}|{last.Kind}|{last.Id.ToString(CultureInfo.InvariantCulture)}":null;
        return Results.Ok(new CommunitySearchPage(results.ToArray(),next));
    }
}
