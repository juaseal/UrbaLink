using System.Security.Claims;
using Npgsql;
using UrbaLink.Contracts;

namespace UrbaLink.Api;

public static partial class MarketEndpoints
{
    private static void MapServiceChats(WebApplication app)
    {
        var g=app.MapGroup("/api/communities/{communityId:guid}/services/{postId:long}").RequireAuthorization().RequireRateLimiting("api");
        g.MapGet("",GetService);
        g.MapPost("/conversations",StartServiceConversation);
        g.MapGet("/conversations/{conversationId:long}/messages",ServiceMessages);
        g.MapPost("/conversations/{conversationId:long}/messages",SendServiceMessage);
        g.MapPost("/conversations/{conversationId:long}/messages/read",ReadServiceMessages);
    }
    private static async Task<IResult> WithService(Guid communityId,long postId,ClaimsPrincipal user,NpgsqlDataSource source,
        Func<NpgsqlConnection,PostDto,Guid,Task<IResult>> action)
    {
        var uid=Tokens.UserId(user);
        await using var c=await source.OpenConnectionAsync();await using var tx=await c.BeginTransactionAsync();
        if(await Access(c,communityId,uid) is null)return Error("No tienes acceso a esta comunidad.",403);
        PostDto post;
        await using(var q=Q(c,"""
            SELECT p.id,p.community_id,p.author_id,u.name,p.title,p.body,p.category,p.created_at
            FROM posts p JOIN users u ON u.id=p.author_id
            WHERE p.id=$1 AND p.community_id=$2 AND p.category='Ofrezco' AND p.deleted_at IS NULL FOR UPDATE OF p
            """,postId,communityId))
        await using(var r=await q.ExecuteReaderAsync())
        {
            if(!await r.ReadAsync())return Error("Servicio no disponible.",404);
            post=new(r.GetInt64(0),r.GetGuid(1),r.GetGuid(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),new DateTimeOffset(r.GetDateTime(7)));
        }
        var result=await action(c,post,uid);await tx.CommitAsync();return result;
    }
    private static async Task<MarketConversationDto[]> ServiceConversations(NpgsqlConnection c,PostDto post,Guid uid)
    {
        await using var q=Q(c,"""
            SELECT t.id,t.interested_user_id,u.name,(SELECT body FROM market_messages WHERE conversation_id=t.id ORDER BY id DESC LIMIT 1)
            FROM market_conversations t JOIN users u ON u.id=t.interested_user_id
            WHERE t.post_id=$1 AND ($2 OR t.interested_user_id=$3) ORDER BY t.id DESC
            """,post.Id,post.AuthorId==uid,uid);
        var result=new List<MarketConversationDto>();await using var r=await q.ExecuteReaderAsync();
        while(await r.ReadAsync())result.Add(new(r.GetInt64(0),r.GetGuid(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3)));
        return result.ToArray();
    }
    private static async Task<bool> CanReadService(NpgsqlConnection c,PostDto post,long conversationId,Guid uid)=>
        (bool)(await Scalar(c,"SELECT EXISTS(SELECT 1 FROM market_conversations WHERE id=$1 AND post_id=$2 AND ($3 OR interested_user_id=$4))",conversationId,post.Id,post.AuthorId==uid,uid))!;
    private static Task<IResult> GetService(Guid communityId,long postId,ClaimsPrincipal user,NpgsqlDataSource source)=>
        WithService(communityId,postId,user,source,async(c,post,uid)=>Results.Ok(new ServiceDetail(post,await ServiceConversations(c,post,uid))));
    private static Task<IResult> StartServiceConversation(Guid communityId,long postId,ClaimsPrincipal user,NpgsqlDataSource source)=>
        WithService(communityId,postId,user,source,async(c,post,uid)=>
        {
            if(post.AuthorId==uid)return Error("Responde desde las conversaciones de tus interesados.",400);
            await Execute(c,"INSERT INTO market_conversations(post_id,interested_user_id) VALUES($1,$2) ON CONFLICT(post_id,interested_user_id) DO NOTHING",postId,uid);
            return Results.Ok((await ServiceConversations(c,post,uid)).Single());
        });
    private static Task<IResult> ServiceMessages(Guid communityId,long postId,long conversationId,long? before,ClaimsPrincipal user,NpgsqlDataSource source)=>
        WithService(communityId,postId,user,source,async(c,post,uid)=>
        {
            if(!await CanReadService(c,post,conversationId,uid))return Error("Conversación no disponible.",404);
            if(before is <=0)return Error("Cursor no válido.",400);
            await using var q=Q(c,"""
                SELECT m.id,m.author_id,u.name,m.body,m.created_at FROM market_messages m JOIN users u ON u.id=m.author_id
                WHERE m.conversation_id=$1 AND m.id<$2 ORDER BY m.id DESC LIMIT 21
                """,conversationId,before??long.MaxValue);
            var messages=new List<MarketMessageDto>();await using var r=await q.ExecuteReaderAsync();
            while(await r.ReadAsync())messages.Add(ReadMessage(r));
            var more=messages.Count>20;if(more)messages.RemoveAt(20);
            return Results.Ok(new MarketMessagePage(messages.ToArray(),more?messages[^1].Id:null));
        });
    private static Task<IResult> SendServiceMessage(Guid communityId,long postId,long conversationId,MarketMessageRequest request,ClaimsPrincipal user,NpgsqlDataSource source)=>
        WithService(communityId,postId,user,source,async(c,post,uid)=>
        {
            if(!await CanReadService(c,post,conversationId,uid))return Error("Conversación no disponible.",404);
            var body=request.Body?.Trim()??"";
            if(request.RequestId==Guid.Empty||body.Length is <1 or >2000)return Error("Escribe un mensaje de 1 a 2000 caracteres.",400);
            var old=await Scalar(c,"SELECT body FROM market_messages WHERE conversation_id=$1 AND author_id=$2 AND request_id=$3",conversationId,uid,request.RequestId);
            if(old is string previous)return previous==body?Results.NoContent():Error("Este envío ya contiene otro mensaje.");
            await Execute(c,"INSERT INTO market_messages(conversation_id,author_id,request_id,body) VALUES($1,$2,$3,$4)",conversationId,uid,request.RequestId,body);
            return Results.NoContent();
        });
    private static Task<IResult> ReadServiceMessages(Guid communityId,long postId,long conversationId,MessageReadRequest request,ClaimsPrincipal user,NpgsqlDataSource source)=>
        WithService(communityId,postId,user,source,async(c,post,uid)=>
        {
            if(!await CanReadService(c,post,conversationId,uid))return Error("Conversación no disponible.",404);
            if(!(bool)(await Scalar(c,"SELECT EXISTS(SELECT 1 FROM market_messages WHERE conversation_id=$1 AND id=$2)",conversationId,request.UpToId))!)return Error("Mensaje no disponible.",400);
            await Execute(c,"""
                INSERT INTO market_message_reads(conversation_id,user_id,last_message_id) VALUES($1,$2,$3)
                ON CONFLICT(conversation_id,user_id) DO UPDATE SET last_message_id=GREATEST(market_message_reads.last_message_id,EXCLUDED.last_message_id)
                """,conversationId,uid,request.UpToId);
            return Results.NoContent();
        });
}
