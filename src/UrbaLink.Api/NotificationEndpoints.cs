using System.Security.Claims;
using Npgsql;
using UrbaLink.Contracts;

namespace UrbaLink.Api;

public static class NotificationEndpoints
{
    public static void MapNotifications(this WebApplication app)
    {
        var group=app.MapGroup("/api/notifications").RequireAuthorization().RequireRateLimiting("api");
        group.MapGet("",Notifications);
        group.MapGet("/count",Count);
    }

    private const string Unread = """
            WITH unread AS (
                SELECT t.id,COALESCE(i.id,p.id,n.id,b.id) AS subject_id,COALESCE(i.title,p.title,b.title,CASE WHEN n.species='Dog' THEN 'Perro' ELSE 'Gato' END || CASE WHEN n.kind='Lost' THEN ' perdido' ELSE ' encontrado' END) AS title,
                    COALESCE(i.community_id,p.community_id,n.community_id,b.community_id) AS community_id,c.name AS community_name,a.role,
                    CASE WHEN t.book_id IS NOT NULL THEN 'Book' WHEN t.animal_notice_id IS NOT NULL THEN 'Animal' WHEN t.post_id IS NULL THEN 'Object' ELSE 'Service' END AS kind,
                    count(*)::int AS unread_count,max(m.id) AS latest
                FROM market_conversations t
                LEFT JOIN market_items i ON i.id=t.item_id
                LEFT JOIN posts p ON p.id=t.post_id AND p.category='Ofrezco' AND p.deleted_at IS NULL
                LEFT JOIN animal_notices n ON n.id=t.animal_notice_id
                LEFT JOIN books b ON b.id=t.book_id
                JOIN communities c ON c.id=COALESCE(i.community_id,p.community_id,n.community_id,b.community_id)
                JOIN active_memberships a ON a.community_id=c.id AND a.user_id=$1
                LEFT JOIN market_message_reads r ON r.conversation_id=t.id AND r.user_id=$1
                JOIN market_messages m ON m.conversation_id=t.id AND m.author_id<>$1 AND m.id>COALESCE(r.last_message_id,0)
                WHERE COALESCE(i.owner_id,p.author_id,n.author_id,b.owner_id)=$1 OR t.interested_user_id=$1
                GROUP BY t.id,i.id,p.id,n.id,b.id,c.name,a.role
            )
        """;

    private static async Task<IResult> Count(ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)
    {
        await using var c=await source.OpenConnectionAsync(ct);
        await using var q=Database.Command(c,Unread+" SELECT COALESCE(sum(unread_count),0)::int,COALESCE(max(latest),0)::bigint FROM unread",Tokens.UserId(user));
        await using var reader=await q.ExecuteReaderAsync(ct);await reader.ReadAsync(ct);
        return Results.Ok(new NotificationCount(reader.GetInt32(0),reader.GetInt64(1)));
    }

    private static async Task<IResult> Notifications(ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)
    {
        await using var c=await source.OpenConnectionAsync(ct);
        await using var q=Database.Command(c,Unread+"""
            SELECT n.community_id,n.community_name,n.role,n.subject_id,n.title,n.id,n.unread_count,u.name,m.body,
                sum(n.unread_count) OVER()::int,n.kind
            FROM unread n JOIN market_messages m ON m.id=n.latest JOIN users u ON u.id=m.author_id
            ORDER BY n.latest DESC LIMIT 50
            """,Tokens.UserId(user));
        var items=new List<MessageNotification>();var count=0;
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
        {
            items.Add(new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetString(4),r.GetInt64(5),r.GetInt32(6),r.GetString(7),r.GetString(8),r.GetString(10)));
            count=r.GetInt32(9);
        }
        return Results.Ok(new NotificationInbox(count,items.ToArray()));
    }

}
