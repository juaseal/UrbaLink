using System.Security.Claims;
using Npgsql;
using UrbaLink.Contracts;

namespace UrbaLink.Api;

public static partial class MarketEndpoints
{
    private static async Task<IResult> Notifications(ClaimsPrincipal user,NpgsqlDataSource source)
    {
        await using var c=await source.OpenConnectionAsync();
        await using var q=Q(c,"""
            WITH unread AS (
                SELECT t.id,COALESCE(i.id,p.id) AS subject_id,COALESCE(i.title,p.title) AS title,
                    COALESCE(i.community_id,p.community_id) AS community_id,c.name AS community_name,a.role,
                    CASE WHEN t.post_id IS NULL THEN 'Object' ELSE 'Service' END AS kind,
                    count(*)::int AS unread_count,max(m.id) AS latest
                FROM market_conversations t
                LEFT JOIN market_items i ON i.id=t.item_id
                LEFT JOIN posts p ON p.id=t.post_id AND p.category='Ofrezco' AND p.deleted_at IS NULL
                JOIN communities c ON c.id=COALESCE(i.community_id,p.community_id)
                JOIN active_memberships a ON a.community_id=c.id AND a.user_id=$1
                LEFT JOIN market_message_reads r ON r.conversation_id=t.id AND r.user_id=$1
                JOIN market_messages m ON m.conversation_id=t.id AND m.author_id<>$1 AND m.id>COALESCE(r.last_message_id,0)
                WHERE COALESCE(i.owner_id,p.author_id)=$1 OR t.interested_user_id=$1
                GROUP BY t.id,i.id,p.id,c.name,a.role
            )
            SELECT n.community_id,n.community_name,n.role,n.subject_id,n.title,n.id,n.unread_count,u.name,m.body,
                sum(n.unread_count) OVER()::int,n.kind
            FROM unread n JOIN market_messages m ON m.id=n.latest JOIN users u ON u.id=m.author_id
            ORDER BY n.latest DESC LIMIT 50
            """,Tokens.UserId(user));
        var items=new List<MessageNotification>();var count=0;
        await using var r=await q.ExecuteReaderAsync();
        while(await r.ReadAsync())
        {
            items.Add(new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetString(4),r.GetInt64(5),r.GetInt32(6),r.GetString(7),r.GetString(8),r.GetString(10)));
            count=r.GetInt32(9);
        }
        return Results.Ok(new NotificationInbox(count,items.ToArray()));
    }

    private static Task<IResult> MarkRead(Guid communityId,long id,long conversationId,MessageReadRequest request,ClaimsPrincipal user,NpgsqlDataSource source) =>
        Mutate(communityId,id,user,source,async(c,item,uid,_)=>
        {
            if(!await CanReadConversation(c,id,conversationId,uid,item.OwnerId))return Error("Conversación no disponible.",404);
            if(request.UpToId<=0 || !(bool)(await Scalar(c,"SELECT EXISTS(SELECT 1 FROM market_messages WHERE conversation_id=$1 AND id=$2)",conversationId,request.UpToId))!)
                return Error("Mensaje no disponible.",400);
            // A read acknowledgement never consumes messages that arrived after the displayed page.
            await Execute(c,"""
                INSERT INTO market_message_reads(conversation_id,user_id,last_message_id) VALUES($1,$2,$3)
                ON CONFLICT(conversation_id,user_id) DO UPDATE
                SET last_message_id=GREATEST(market_message_reads.last_message_id,EXCLUDED.last_message_id)
                """,conversationId,uid,request.UpToId);
            return Results.NoContent();
        });
}
