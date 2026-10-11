using System.Security.Claims;
using Npgsql;
using UrbaLink.Contracts;

namespace UrbaLink.Api;

public static partial class MarketEndpoints
{
    private static async Task<MarketConversationDto[]> Conversations(NpgsqlConnection c,long itemId,Guid owner,Guid viewer)
    {
        await using var q=Q(c,"""
            SELECT t.id,t.interested_user_id,u.name,
                (SELECT body FROM market_messages WHERE conversation_id=t.id ORDER BY id DESC LIMIT 1)
            FROM market_conversations t JOIN users u ON u.id=t.interested_user_id
            WHERE t.item_id=$1 AND ($2 OR t.interested_user_id=$3) ORDER BY t.id DESC
            """,itemId,owner==viewer,viewer);
        var result=new List<MarketConversationDto>();
        await using var r=await q.ExecuteReaderAsync();
        while(await r.ReadAsync()) result.Add(new(r.GetInt64(0),r.GetGuid(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3)));
        return result.ToArray();
    }

    private static Task<IResult> StartConversation(Guid communityId,long id,StartMarketConversation request,ClaimsPrincipal user,NpgsqlDataSource source) =>
        Mutate(communityId,id,user,source,async(c,item,uid,_)=>
        {
            var interested=request.InterestedUserId??uid;
            if(interested==item.OwnerId || (uid!=item.OwnerId && interested!=uid))
                return Error("No puedes iniciar esta conversación.",403);
            var existing=(await Conversations(c,id,item.OwnerId,uid)).FirstOrDefault(t=>t.InterestedUserId==interested);
            if(existing is not null) return Results.Ok(existing);
            // The owner can start a conversation with the auction winner. Other conversations
            // are initiated by interested neighbors, never by impersonating a participant.
            if(uid==item.OwnerId && interested!=item.WinnerId) return Error("No puedes iniciar esta conversación.",403);
            if(item.State!="Active" && !(item.Mode=="Auction" && item.State is "Ended" or "Completed" && interested==item.WinnerId))
                return Error("Este objeto ya no admite nuevos interesados.");
            if(await Access(c,communityId,interested) is null) return Error("El vecino ya no pertenece a la comunidad.",404);
            await Execute(c,"INSERT INTO market_conversations(item_id,interested_user_id) VALUES($1,$2)",id,interested);
            return Results.Ok((await Conversations(c,id,item.OwnerId,uid)).Single(t=>t.InterestedUserId==interested));
        });

    private static async Task<bool> CanReadConversation(NpgsqlConnection c,long itemId,long conversationId,Guid viewer,Guid owner) =>
        (bool)(await Scalar(c,"""
            SELECT EXISTS(SELECT 1 FROM market_conversations
                WHERE id=$1 AND item_id=$2 AND ($3=$4 OR interested_user_id=$3))
            """,conversationId,itemId,viewer,owner))!;

    private static MarketMessageDto ReadMessage(NpgsqlDataReader r) =>
        new(r.GetInt64(0),r.GetGuid(1),r.GetString(2),r.GetString(3),new DateTimeOffset(r.GetDateTime(4)));

    private static async Task<IResult> Messages(Guid communityId,long id,long conversationId,long? before,ClaimsPrincipal user,NpgsqlDataSource source)
    {
        if(before is <=0) return Error("Cursor no válido.",400);
        var uid=Tokens.UserId(user);
        await using var c=await source.OpenConnectionAsync();
        await using var tx=await c.BeginTransactionAsync();
        if(await Access(c,communityId,uid) is null) return Error("No tienes acceso a esta comunidad.",403);
        var item=await Item(c,communityId,id,uid);
        if(item is null || !await CanReadConversation(c,id,conversationId,uid,item.OwnerId))
            return Error("Conversación no disponible.",404);
        await using var q=Q(c,"""
            SELECT m.id,m.author_id,u.name,m.body,m.created_at FROM market_messages m JOIN users u ON u.id=m.author_id
            WHERE conversation_id=$1 AND m.id<$2 ORDER BY m.id DESC LIMIT 21
            """,conversationId,before??long.MaxValue);
        var result=new List<MarketMessageDto>();
        await using var r=await q.ExecuteReaderAsync();
        while(await r.ReadAsync()) result.Add(ReadMessage(r));
        var more=result.Count>20;
        if(more) result.RemoveAt(20);
        return Results.Ok(new MarketMessagePage(result.ToArray(),more?result[^1].Id:null));
    }

    private static Task<IResult> SendMessage(Guid communityId,long id,long conversationId,MarketMessageRequest request,ClaimsPrincipal user,NpgsqlDataSource source) =>
        Mutate(communityId,id,user,source,async(c,item,uid,_)=>
        {
            if(!await CanReadConversation(c,id,conversationId,uid,item.OwnerId)) return Error("Conversación no disponible.",404);
            var text=request.Body?.Trim()??"";
            if(request.RequestId==Guid.Empty || text.Length is <1 or >2000) return Error("Escribe un mensaje de 1 a 2000 caracteres.",400);
            var old=await Scalar(c,"SELECT body FROM market_messages WHERE conversation_id=$1 AND author_id=$2 AND request_id=$3",conversationId,uid,request.RequestId);
            if(old is string previous) return previous==text?Results.NoContent():Error("Este envío ya contiene otro mensaje.");
            await Execute(c,"INSERT INTO market_messages(conversation_id,author_id,request_id,body) VALUES($1,$2,$3,$4)",conversationId,uid,request.RequestId,text);
            return Results.NoContent();
        });
}
