using System.Security.Claims;
using Npgsql;
using UrbaLink.Contracts;

namespace UrbaLink.Api;

public static class ServiceChatEndpoints
{
    private static IResult Error(string text, int status = 409) => Results.Json(new ApiError(text), statusCode: status);

    public static void MapServiceChats(this WebApplication app)
    {
        var g=app.MapGroup("/api/communities/{communityId:guid}/services/{postId:long}").RequireAuthorization().RequireRateLimiting("api");
        g.MapGet("",GetService);
        g.MapPost("/conversations",StartServiceConversation);
        g.MapGet("/conversations/{conversationId:long}/messages",ServiceMessages);
        g.MapPost("/conversations/{conversationId:long}/messages",SendServiceMessage);
        g.MapPost("/conversations/{conversationId:long}/messages/read",ReadServiceMessages);
    }
    private static async Task<IResult> WithService(Guid communityId,long postId,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct,
        Func<NpgsqlConnection,PostDto,Guid,Task<IResult>> action)
    {
        var uid=Tokens.UserId(user);
        await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        if(await CommunityAccess.Role(c,communityId,uid,ct) is null)return Error("No tienes acceso a esta comunidad.",403);
        PostDto post;
        await using(var q=Database.Command(c,"""
            SELECT p.id,p.community_id,p.author_id,u.name,p.title,p.body,p.category,p.created_at
            FROM posts p JOIN users u ON u.id=p.author_id
            WHERE p.id=$1 AND p.community_id=$2 AND p.category='Ofrezco' AND p.deleted_at IS NULL FOR SHARE OF p
            """,postId,communityId))
        await using(var r=await q.ExecuteReaderAsync(ct))
        {
            if(!await r.ReadAsync(ct))return Error("Servicio no disponible.",404);
            post=PostRecord.Read(r);
        }
        var result=await action(c,post,uid);await tx.CommitAsync(ct);return result;
    }
    private static async Task<MarketConversationDto[]> ServiceConversations(NpgsqlConnection c,PostDto post,Guid uid,CancellationToken ct)
    {
        await using var q=Database.Command(c,"""
            SELECT t.id,t.interested_user_id,u.name,(SELECT body FROM market_messages WHERE conversation_id=t.id ORDER BY id DESC LIMIT 1)
            FROM market_conversations t JOIN users u ON u.id=t.interested_user_id
            WHERE t.post_id=$1 AND ($2 OR t.interested_user_id=$3) ORDER BY t.id DESC
            """,post.Id,post.AuthorId==uid,uid);
        var result=new List<MarketConversationDto>();await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))result.Add(new(r.GetInt64(0),r.GetGuid(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3)));
        return result.ToArray();
    }
    private static async Task<bool> CanReadService(NpgsqlConnection c,PostDto post,long conversationId,Guid uid,CancellationToken ct)=>
        (bool)(await Database.Scalar(c,ct,"SELECT EXISTS(SELECT 1 FROM market_conversations WHERE id=$1 AND post_id=$2 AND ($3 OR interested_user_id=$4))",conversationId,post.Id,post.AuthorId==uid,uid))!;
    private static Task<IResult> GetService(Guid communityId,long postId,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        WithService(communityId,postId,user,source,ct,async(c,post,uid)=>Results.Ok(new ServiceDetail(post,await ServiceConversations(c,post,uid,ct))));
    private static Task<IResult> StartServiceConversation(Guid communityId,long postId,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        WithService(communityId,postId,user,source,ct,async(c,post,uid)=>
        {
            if(post.AuthorId==uid)return Error("Responde desde las conversaciones de tus interesados.",400);
            await Database.Execute(c,ct,"INSERT INTO market_conversations(post_id,interested_user_id) VALUES($1,$2) ON CONFLICT(post_id,interested_user_id) DO NOTHING",postId,uid);
            return Results.Ok((await ServiceConversations(c,post,uid,ct)).Single());
        });
    private static Task<IResult> ServiceMessages(Guid communityId,long postId,long conversationId,long? before,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        WithService(communityId,postId,user,source,ct,async(c,post,uid)=>
            !await CanReadService(c,post,conversationId,uid,ct) ? Error("Conversación no disponible.",404)
            : await ConversationMessages.List(c,conversationId,before,ct));

    private static Task<IResult> SendServiceMessage(Guid communityId,long postId,long conversationId,MarketMessageRequest request,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        WithService(communityId,postId,user,source,ct,async(c,post,uid)=>
            !await CanReadService(c,post,conversationId,uid,ct) ? Error("Conversación no disponible.",404)
            : await ConversationMessages.Send(c,conversationId,uid,request,ct));

    private static Task<IResult> ReadServiceMessages(Guid communityId,long postId,long conversationId,MessageReadRequest request,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        WithService(communityId,postId,user,source,ct,async(c,post,uid)=>
            !await CanReadService(c,post,conversationId,uid,ct) ? Error("Conversación no disponible.",404)
            : await ConversationMessages.MarkRead(c,conversationId,uid,request,ct));
}
