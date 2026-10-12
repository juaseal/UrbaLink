using System.Security.Claims;
using Npgsql;
using UrbaLink.Contracts;

namespace UrbaLink.Api;

public static partial class MarketEndpoints
{
    private static async Task<MarketConversationDto[]> Conversations(NpgsqlConnection c,long itemId,Guid owner,Guid viewer,CancellationToken ct)
    {
        await using var q=Database.Command(c,"""
            SELECT t.id,t.interested_user_id,u.name,
                (SELECT body FROM market_messages WHERE conversation_id=t.id ORDER BY id DESC LIMIT 1)
            FROM market_conversations t JOIN users u ON u.id=t.interested_user_id
            WHERE t.item_id=$1 AND ($2 OR t.interested_user_id=$3) ORDER BY t.id DESC
            """,itemId,owner==viewer,viewer);
        var result=new List<MarketConversationDto>();
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)) result.Add(new(r.GetInt64(0),r.GetGuid(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3)));
        return result.ToArray();
    }

    private static Task<IResult> StartConversation(Guid communityId,long id,StartMarketConversation request,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct) =>
        Mutate(communityId,id,user,source,ct,async(c,item,uid,_)=>
        {
            var interested=request.InterestedUserId??uid;
            if(interested==item.OwnerId || (uid!=item.OwnerId && interested!=uid))
                return Error("No puedes iniciar esta conversación.",403);
            var existing=(await Conversations(c,id,item.OwnerId,uid,ct)).FirstOrDefault(t=>t.InterestedUserId==interested);
            if(existing is not null) return Results.Ok(existing);
            // The owner can start a conversation with the auction winner. Other conversations
            // are initiated by interested neighbors, never by impersonating a participant.
            if(uid==item.OwnerId && interested!=item.WinnerId) return Error("No puedes iniciar esta conversación.",403);
            if(item.State!="Active" && !(item.Mode=="Auction" && item.State is "Ended" or "Completed" && interested==item.WinnerId))
                return Error("Este objeto ya no admite nuevos interesados.");
            if(await Access(c,communityId,interested,ct) is null) return Error("El vecino ya no pertenece a la comunidad.",404);
            await Database.Execute(c,ct,"INSERT INTO market_conversations(item_id,interested_user_id) VALUES($1,$2)",id,interested);
            return Results.Ok((await Conversations(c,id,item.OwnerId,uid,ct)).Single(t=>t.InterestedUserId==interested));
        });

    private static async Task<bool> CanReadConversation(NpgsqlConnection c,long itemId,long conversationId,Guid viewer,Guid owner,CancellationToken ct) =>
        (bool)(await Database.Scalar(c,ct,"""
            SELECT EXISTS(SELECT 1 FROM market_conversations
                WHERE id=$1 AND item_id=$2 AND ($3=$4 OR interested_user_id=$3))
            """,conversationId,itemId,viewer,owner))!;

    private static async Task<IResult> WithMarketChat(Guid communityId,long id,long conversationId,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct,
        Func<NpgsqlConnection,Guid,Task<IResult>> action)
    {
        var uid=Tokens.UserId(user);
        await using var c=await source.OpenConnectionAsync(ct);
        await using var tx=await c.BeginTransactionAsync(ct);
        if(await CommunityAccess.Role(c,communityId,uid,ct) is null) return Error("No tienes acceso a esta comunidad.",403);
        // Chats stay available after withdrawal/completion; do not lock the item against bids.
        var owner=await Database.Scalar(c,ct,"SELECT owner_id FROM market_items WHERE community_id=$1 AND id=$2",communityId,id);
        if(owner is not Guid ownerId || !await CanReadConversation(c,id,conversationId,uid,ownerId,ct))
            return Error("Conversación no disponible.",404);
        var result=await action(c,uid);
        await tx.CommitAsync(ct);
        return result;
    }

    private static Task<IResult> Messages(Guid communityId,long id,long conversationId,long? before,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        WithMarketChat(communityId,id,conversationId,user,source,ct,(c,_)=>ConversationMessages.List(c,conversationId,before,ct));

    private static Task<IResult> SendMessage(Guid communityId,long id,long conversationId,MarketMessageRequest request,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        WithMarketChat(communityId,id,conversationId,user,source,ct,(c,uid)=>ConversationMessages.Send(c,conversationId,uid,request,ct));

    private static Task<IResult> MarkRead(Guid communityId,long id,long conversationId,MessageReadRequest request,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        WithMarketChat(communityId,id,conversationId,user,source,ct,(c,uid)=>ConversationMessages.MarkRead(c,conversationId,uid,request,ct));
}
