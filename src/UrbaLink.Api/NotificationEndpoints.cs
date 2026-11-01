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
                SELECT t.id,t.subject_id,t.title,t.community_id,c.name AS community_name,a.role,t.kind,
                    count(*)::int AS unread_count,max(m.id) AS latest
                FROM conversation_subjects t
                JOIN communities c ON c.id=t.community_id
                JOIN active_memberships a ON a.community_id=c.id AND a.user_id=$1
                LEFT JOIN market_message_reads r ON r.conversation_id=t.id AND r.user_id=$1
                JOIN market_messages m ON m.conversation_id=t.id AND m.author_id<>$1 AND m.id>COALESCE(r.last_message_id,0)
                WHERE t.owner_id=$1 OR t.interested_user_id=$1
                GROUP BY t.id,t.subject_id,t.title,t.community_id,t.kind,c.name,a.role
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
