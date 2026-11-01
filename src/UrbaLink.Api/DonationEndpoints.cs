using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using UrbaLink.Contracts;
namespace UrbaLink.Api;

public static class DonationEndpoints
{
    private static IResult Error(string message,int status=400)=>Results.Json(new ApiError(message),statusCode:status);
    private const string Select="""
        SELECT b.id,b.community_id,b.owner_id,u.name,b.title,b.season,b.size,b.condition,b.audience,b.quantity,b.description,b.photo,b.state,b.version
        FROM clothing_donations b JOIN users u ON u.id=b.owner_id
        """;
    private static DonationDto Read(NpgsqlDataReader r)=>new(r.GetInt64(0),r.GetGuid(1),r.GetGuid(2),r.GetString(3),r.GetString(4),r.GetString(5),
        r.GetString(6),r.GetString(7),r.GetString(8),r.GetInt32(9),r.GetString(10),r.IsDBNull(11)?null:r.GetFieldValue<byte[]>(11),r.GetString(12),r.GetInt32(13));
    private static async Task<DonationDto?> Get(NpgsqlConnection c,Guid communityId,long id,CancellationToken ct)
    {
        await using var q=Database.Command(c,Select+" WHERE b.community_id=$1 AND b.id=$2",communityId,id);
        await using var r=await q.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?Read(r):null;
    }
    public static void MapDonations(this WebApplication app)
    {
        var g=app.MapGroup("/api/communities/{communityId:guid}/donations").RequireAuthorization().RequireRateLimiting("api");
        g.MapGet("",List);g.MapPost("",Create);g.MapGet("/{id:long}",Detail);g.MapPut("/{id:long}",Edit);g.MapPost("/{id:long}/state",State);
        g.MapPost("/{id:long}/conversations",Start);
        g.MapGet("/{id:long}/conversations/{conversationId:long}/messages",Messages);
        g.MapPost("/{id:long}/conversations/{conversationId:long}/messages",Send);
        g.MapPost("/{id:long}/conversations/{conversationId:long}/messages/read",MarkRead);
    }
    private static async Task<IResult> List(Guid communityId,string? audience,string? season,string? size,string? scope,long? before,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)
    {
        if(audience is not(null or "Adult" or "Young" or "Child" or "Baby")||season is not(null or "AllYear" or "Winter" or "Summer" or "MidSeason")||size?.Length>40||scope is not(null or "Mine")||before is <=0)return Error("Filtro de ropa no válido.");
        await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        var uid=Tokens.UserId(user);if(await CommunityAccess.Role(c,communityId,uid,ct) is null)return Error("No tienes acceso a esta comunidad.",403);
        await using var q=Database.Command(c,Select+" "+"""
            WHERE b.community_id=$1 AND ($2='' OR b.audience=$2) AND ($3='' OR b.season=$3) AND b.id<$4
              AND (($5='' AND b.state IN ('Available','Reserved')) OR ($5='Mine' AND b.owner_id=$6))
              AND ($7='' OR strpos(lower(b.size),lower($7))>0) ORDER BY b.id DESC LIMIT 21
            """,communityId,audience??"",season??"",before??long.MaxValue,scope??"",uid,size?.Trim()??"");
        var items=new List<DonationDto>();await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))items.Add(Read(r));
        var more=items.Count>20;if(more)items.RemoveAt(20);return Results.Ok(new DonationPage(items.ToArray(),more?items[^1].Id:null));
    }
    private static async Task<IResult> Create(Guid communityId,DonationInput input,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)
    {
        var request=DonationRules.Normalize(input);if(DonationRules.Validate(request) is {} error)return Error(error);
        await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);var uid=Tokens.UserId(user);
        if(await CommunityAccess.Role(c,communityId,uid,ct) is null)return Error("No tienes acceso a esta comunidad.",403);
        await Database.Scalar(c,ct,"SELECT pg_advisory_xact_lock(hashtextextended($1,0))",$"donation:{communityId}:{uid}:{request.RequestId}");
        var hash=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
        await using(var q=Database.Command(c,"SELECT id,request_hash FROM clothing_donations WHERE community_id=$1 AND owner_id=$2 AND request_id=$3",communityId,uid,request.RequestId))
        await using(var r=await q.ExecuteReaderAsync(ct))
        {
            if(await r.ReadAsync(ct))
            {
                var id=r.GetInt64(0);var matches=r.GetString(1)==hash;await r.DisposeAsync();
                return matches?Results.Ok(await Get(c,communityId,id,ct)):Error("Este envío ya existe con otros datos.",409);
            }
        }
        var created=(long)(await Database.Scalar(c,ct,"""
            INSERT INTO clothing_donations(community_id,owner_id,request_id,request_hash,title,season,size,condition,audience,quantity,description,photo)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12) RETURNING id
            """,communityId,uid,request.RequestId,hash,request.Title,request.Season,request.Size,request.Condition,request.Audience,request.Quantity,request.Description,request.Photo))!;
        var donation=await Get(c,communityId,created,ct);await tx.CommitAsync(ct);return Results.Ok(donation);
    }
    private static async Task<IResult> With(Guid communityId,long id,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct,bool write,
        Func<NpgsqlConnection,DonationDto,Guid,string,Task<IResult>> action)
    {
        await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);var uid=Tokens.UserId(user);
        var role=await CommunityAccess.Role(c,communityId,uid,ct);if(role is null)return Error("No tienes acceso a esta comunidad.",403);
        DonationDto donation;await using(var q=Database.Command(c,Select+" WHERE b.community_id=$1 AND b.id=$2 "+(write?"FOR UPDATE OF b":"FOR SHARE OF b"),communityId,id))
        await using(var r=await q.ExecuteReaderAsync(ct)){if(!await r.ReadAsync(ct))return Error("Anuncio no disponible.",404);donation=Read(r);}
        var result=await action(c,donation,uid,role);await tx.CommitAsync(ct);return result;
    }
    private static async Task<MarketConversationDto[]> Chats(NpgsqlConnection c,DonationDto donation,Guid uid,CancellationToken ct)
    {
        await using var q=Database.Command(c,"""
            SELECT t.id,t.interested_user_id,u.name,(SELECT body FROM market_messages WHERE conversation_id=t.id ORDER BY id DESC LIMIT 1)
            FROM market_conversations t JOIN users u ON u.id=t.interested_user_id WHERE t.donation_id=$1 AND ($2 OR t.interested_user_id=$3) ORDER BY t.id DESC
            """,donation.Id,donation.OwnerId==uid,uid);
        var list=new List<MarketConversationDto>();await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))list.Add(new(r.GetInt64(0),r.GetGuid(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3)));return list.ToArray();
    }
    private static Task<IResult> Detail(Guid communityId,long id,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        With(communityId,id,user,source,ct,false,async(c,b,uid,_)=>Results.Ok(new DonationDetail(b,await Chats(c,b,uid,ct))));
    private static Task<IResult> Edit(Guid communityId,long id,int version,DonationInput input,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        With(communityId,id,user,source,ct,true,async(c,b,uid,_)=>
        {
            if(b.OwnerId!=uid)return Error("Solo quien publica puede editar el anuncio.",403);
            if(b.Version!=version||b.State is "Withdrawn" or "Given")return Error("El anuncio ha cambiado o ya no se puede editar. Actualiza su ficha.",409);
            var r=DonationRules.Normalize(input);if(DonationRules.Validate(r) is {} error)return Error(error);
            await Database.Execute(c,ct,"""
                UPDATE clothing_donations SET title=$2,season=$3,size=$4,condition=$5,audience=$6,quantity=$7,description=$8,photo=$9,version=version+1 WHERE id=$1
                """,id,r.Title,r.Season,r.Size,r.Condition,r.Audience,r.Quantity,r.Description,r.Photo);return Results.NoContent();
        });
    private static Task<IResult> State(Guid communityId,long id,DonationStateRequest request,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        With(communityId,id,user,source,ct,true,async(c,b,uid,role)=>
        {
            if(b.OwnerId!=uid&&(role!="Admin"||request.State!="Withdrawn"))return Error("No puedes cambiar este anuncio.",403);
            if(request.State is not("Available" or "Reserved" or "Given" or "Withdrawn"))return Error("Estado no válido.");
            if(request.State==b.State)return Results.NoContent();
            if(b.State is "Withdrawn" or "Given")return Error("Este anuncio ya está cerrado.",409);
            await Database.Execute(c,ct,"UPDATE clothing_donations SET state=$2,version=version+1 WHERE id=$1",id,request.State);return Results.NoContent();
        });
    private static Task<IResult> Start(Guid communityId,long id,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        With(communityId,id,user,source,ct,true,async(c,b,uid,_)=>
        {
            if(b.OwnerId==uid)return Error("Responde desde tus conversaciones.");
            var old=(await Chats(c,b,uid,ct)).FirstOrDefault();if(old is not null)return Results.Ok(old);
            if(b.State is "Withdrawn" or "Given")return Error("Este anuncio ya no admite nuevas solicitudes.",409);
            await Database.Execute(c,ct,"INSERT INTO market_conversations(donation_id,interested_user_id) VALUES($1,$2) ON CONFLICT(donation_id,interested_user_id) DO NOTHING",id,uid);
            return Results.Ok((await Chats(c,b,uid,ct)).Single());
        });
    private static Task<IResult> Chat(Guid communityId,long id,long conversationId,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct,
        Func<NpgsqlConnection,Guid,Task<IResult>> action)=>With(communityId,id,user,source,ct,false,async(c,b,uid,_)=>
        {
            if(!(bool)(await Database.Scalar(c,ct,"SELECT EXISTS(SELECT 1 FROM market_conversations WHERE id=$1 AND donation_id=$2 AND ($3 OR interested_user_id=$4))",conversationId,id,b.OwnerId==uid,uid))!)return Error("Conversación no disponible.",404);
            return await action(c,uid);
        });
    private static Task<IResult> Messages(Guid communityId,long id,long conversationId,long? before,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        Chat(communityId,id,conversationId,user,source,ct,(c,_)=>ConversationMessages.List(c,conversationId,before,ct));
    private static Task<IResult> Send(Guid communityId,long id,long conversationId,MarketMessageRequest request,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        Chat(communityId,id,conversationId,user,source,ct,(c,uid)=>ConversationMessages.Send(c,conversationId,uid,request,ct));
    private static Task<IResult> MarkRead(Guid communityId,long id,long conversationId,MessageReadRequest request,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        Chat(communityId,id,conversationId,user,source,ct,(c,uid)=>ConversationMessages.MarkRead(c,conversationId,uid,request,ct));
}
