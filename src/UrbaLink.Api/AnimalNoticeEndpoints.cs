using System.Security.Claims;
using System.Text.Json;
using Npgsql;
using UrbaLink.Contracts;
namespace UrbaLink.Api;

public static class AnimalNoticeEndpoints
{
    private static IResult Error(string text,int status=400)=>Results.Json(new ApiError(text),statusCode:status);
    private const string Select = """
        SELECT n.id,n.community_id,n.author_id,u.name,n.kind,n.species,n.name,n.location,n.occurred_at,
        n.description,n.is_with_reporter,n.photo,n.status,n.closed_at,n.version
        FROM animal_notices n JOIN users u ON u.id=n.author_id
        """;
    private static AnimalNoticeDto Read(NpgsqlDataReader r)=>new(r.GetInt64(0),r.GetGuid(1),r.GetGuid(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetFieldValue<DateTimeOffset>(8),r.GetString(9),r.GetBoolean(10),r.IsDBNull(11)?null:r.GetFieldValue<byte[]>(11),r.GetString(12),r.IsDBNull(13)?null:r.GetFieldValue<DateTimeOffset>(13),r.GetInt32(14));
    public static void MapAnimalNotices(this WebApplication app)
    {
        var g=app.MapGroup("/api/communities/{communityId:guid}/animals").RequireAuthorization().RequireRateLimiting("api");
        g.MapGet("",List); g.MapPost("",Create); g.MapGet("/{noticeId:long}",Detail);
        g.MapPut("/{noticeId:long}",Edit); g.MapPost("/{noticeId:long}/close",Close);
        g.MapPost("/{noticeId:long}/conversations",Start);
        g.MapGet("/{noticeId:long}/conversations/{conversationId:long}/messages",Messages);
        g.MapPost("/{noticeId:long}/conversations/{conversationId:long}/messages",Send);
        g.MapPost("/{noticeId:long}/conversations/{conversationId:long}/messages/read",MarkRead);
    }
    private static async Task<IResult> List(Guid communityId,long? before,bool? closed,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)
    {
        if(before is <=0)return Error("Página no válida.");
        await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        if(await CommunityAccess.Role(c,communityId,Tokens.UserId(user),ct) is null)return Error("No tienes acceso a esta comunidad.",403);
        await using var q=Database.Command(c,Select+" WHERE n.community_id=$1 AND n.id<$2 AND ((n.status<>'Active')=$3) ORDER BY n.id DESC LIMIT 21",communityId,before??long.MaxValue,closed??false);
        var items=new List<AnimalNoticeDto>();await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))items.Add(Read(r));
        var more=items.Count>20;if(more)items.RemoveAt(20);return Results.Ok(new AnimalNoticePage(items.ToArray(),more?items[^1].Id:null));
    }
    private static async Task Audit(NpgsqlConnection c,long id,Guid uid,string action,CancellationToken ct)=>
        await Database.Execute(c,ct,"INSERT INTO animal_notice_history(notice_id,actor_id,action,snapshot) SELECT id,$2,$3,to_jsonb(n)-'request_hash'-'photo' FROM animal_notices n WHERE id=$1",id,uid,action);
    private static async Task<IResult> Create(Guid communityId,AnimalNoticeInput input,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)
    {
        var r=AnimalNoticeRules.Normalize(input);if(AnimalNoticeRules.Validate(r) is {} error)return Error(error);
        var uid=Tokens.UserId(user);await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        if(await CommunityAccess.Role(c,communityId,uid,ct) is null)return Error("No tienes acceso a esta comunidad.",403);
        await Database.Scalar(c,ct,"SELECT pg_advisory_xact_lock(hashtextextended($1,0))",$"animal:{communityId}:{uid}:{r.RequestId}");
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(r)));
        var old=await Database.Scalar(c,ct,"SELECT id FROM animal_notices WHERE community_id=$1 AND author_id=$2 AND request_id=$3",communityId,uid,r.RequestId);
        long id;
        if(old is long existing)
        {
            if(!(bool)(await Database.Scalar(c,ct,"SELECT request_hash=$2 FROM animal_notices WHERE id=$1",existing,hash))!)return Error("Este envío ya existe con otros datos.",409);
            id=existing;
        }
        else
        {
            id=(long)(await Database.Scalar(c,ct,"""
                INSERT INTO animal_notices(community_id,author_id,request_id,request_hash,kind,species,name,location,occurred_at,description,is_with_reporter,photo)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12) RETURNING id
                """,communityId,uid,r.RequestId,hash,r.Kind,r.Species,r.Name,r.Location,r.OccurredAt.ToUniversalTime(),r.Description,r.IsWithReporter,r.Photo))!;
            await Audit(c,id,uid,"Created",ct);
        }
        AnimalNoticeDto result;await using(var q=Database.Command(c,Select+" WHERE n.id=$1",id))await using(var reader=await q.ExecuteReaderAsync(ct)){await reader.ReadAsync(ct);result=Read(reader);}
        await tx.CommitAsync(ct);return Results.Ok(result);
    }
    private static async Task<IResult> With(Guid communityId,long noticeId,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct,Func<NpgsqlConnection,AnimalNoticeDto,Guid,string,Task<IResult>> action)
    {
        var uid=Tokens.UserId(user);await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        var role=await CommunityAccess.Role(c,communityId,uid,ct);if(role is null)return Error("No tienes acceso a esta comunidad.",403);
        AnimalNoticeDto n;await using(var q=Database.Command(c,Select+" WHERE n.id=$1 AND n.community_id=$2 FOR UPDATE OF n",noticeId,communityId))await using(var r=await q.ExecuteReaderAsync(ct)){if(!await r.ReadAsync(ct))return Error("Aviso no disponible.",404);n=Read(r);}
        var result=await action(c,n,uid,role);await tx.CommitAsync(ct);return result;
    }
    private static async Task<MarketConversationDto[]> Chats(NpgsqlConnection c,AnimalNoticeDto n,Guid uid,CancellationToken ct)
    {
        await using var q=Database.Command(c,"""
            SELECT t.id,t.interested_user_id,u.name,(SELECT body FROM market_messages WHERE conversation_id=t.id ORDER BY id DESC LIMIT 1)
            FROM market_conversations t JOIN users u ON u.id=t.interested_user_id
            WHERE t.animal_notice_id=$1 AND ($2 OR t.interested_user_id=$3) ORDER BY t.id DESC
            """,n.Id,n.AuthorId==uid,uid);
        var result=new List<MarketConversationDto>();await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))result.Add(new(r.GetInt64(0),r.GetGuid(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3)));return result.ToArray();
    }
    private static Task<IResult> Detail(Guid communityId,long noticeId,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>With(communityId,noticeId,user,source,ct,async(c,n,uid,role)=>Results.Ok(new AnimalNoticeDetail(n,await Chats(c,n,uid,ct))));
    private static Task<IResult> Edit(Guid communityId,long noticeId,int version,AnimalNoticeInput input,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>With(communityId,noticeId,user,source,ct,async(c,n,uid,role)=>
    {
        if(uid!=n.AuthorId)return Error("Solo el autor puede editar el aviso.",403);
        if(n.Status!="Active" || n.Version!=version)return Error("El aviso ha cambiado. Actualiza antes de editar.",409);
        var r=AnimalNoticeRules.Normalize(input);if(AnimalNoticeRules.Validate(r) is {} error)return Error(error);
        await Database.Execute(c,ct,"UPDATE animal_notices SET kind=$2,species=$3,name=$4,location=$5,occurred_at=$6,description=$7,is_with_reporter=$8,photo=$9,updated_at=now(),version=version+1 WHERE id=$1",n.Id,r.Kind,r.Species,r.Name,r.Location,r.OccurredAt.ToUniversalTime(),r.Description,r.IsWithReporter,r.Photo);
        await Audit(c,n.Id,uid,"Edited",ct);return Results.NoContent();
    });
    private static Task<IResult> Close(Guid communityId,long noticeId,CloseAnimalNoticeRequest request,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>With(communityId,noticeId,user,source,ct,async(c,n,uid,role)=>
    {
        if(uid!=n.AuthorId && (role!="Admin" || request.Status!="Withdrawn"))return Error("No puedes cerrar este aviso.",403);
        if(request.Status is not ("Recovered" or "Withdrawn"))return Error("Cierre no válido.");
        if(n.Status==request.Status)return Results.NoContent();
        if(n.Status!="Active")return Error("El aviso ya está cerrado.",409);
        await Database.Execute(c,ct,"UPDATE animal_notices SET status=$2,closed_at=now(),updated_at=now(),version=version+1 WHERE id=$1",n.Id,request.Status);
        await Audit(c,n.Id,uid,request.Status,ct);return Results.NoContent();
    });
    private static Task<IResult> Start(Guid communityId,long noticeId,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>With(communityId,noticeId,user,source,ct,async(c,n,uid,role)=>
    {
        if(n.AuthorId==uid)return Error("Responde desde tus conversaciones.");
        if(n.Status!="Active")return Error("El aviso ya está cerrado.",409);
        await Database.Execute(c,ct,"INSERT INTO market_conversations(animal_notice_id,interested_user_id) VALUES($1,$2) ON CONFLICT(animal_notice_id,interested_user_id) DO NOTHING",n.Id,uid);
        return Results.Ok((await Chats(c,n,uid,ct)).Single());
    });
    private static async Task<bool> CanRead(NpgsqlConnection c,AnimalNoticeDto n,long chat,Guid uid,CancellationToken ct)=>
        (bool)(await Database.Scalar(c,ct,"SELECT EXISTS(SELECT 1 FROM market_conversations WHERE id=$1 AND animal_notice_id=$2 AND ($3 OR interested_user_id=$4))",chat,n.Id,n.AuthorId==uid,uid))!;
    private static Task<IResult> Messages(Guid communityId,long noticeId,long conversationId,long? before,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>With(communityId,noticeId,user,source,ct,async(c,n,uid,role)=>!await CanRead(c,n,conversationId,uid,ct)?Error("Conversación no disponible.",404):await ConversationMessages.List(c,conversationId,before,ct));
    private static Task<IResult> Send(Guid communityId,long noticeId,long conversationId,MarketMessageRequest request,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>With(communityId,noticeId,user,source,ct,async(c,n,uid,role)=>!await CanRead(c,n,conversationId,uid,ct)?Error("Conversación no disponible.",404):await ConversationMessages.Send(c,conversationId,uid,request,ct));
    private static Task<IResult> MarkRead(Guid communityId,long noticeId,long conversationId,MessageReadRequest request,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>With(communityId,noticeId,user,source,ct,async(c,n,uid,role)=>!await CanRead(c,n,conversationId,uid,ct)?Error("Conversación no disponible.",404):await ConversationMessages.MarkRead(c,conversationId,uid,request,ct));
}

