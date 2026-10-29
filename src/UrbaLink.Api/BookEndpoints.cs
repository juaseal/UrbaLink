using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using UrbaLink.Contracts;
namespace UrbaLink.Api;

public static class BookEndpoints
{
    private static IResult Error(string message,int status=400)=>Results.Json(new ApiError(message),statusCode:status);
    private const string Select="""
        SELECT b.id,b.community_id,b.owner_id,u.name,b.title,b.author,b.isbn,b.condition,b.audience,b.mode,b.description,b.photo,b.state,b.version
        FROM books b JOIN users u ON u.id=b.owner_id
        """;
    private static BookDto Read(NpgsqlDataReader r)=>new(r.GetInt64(0),r.GetGuid(1),r.GetGuid(2),r.GetString(3),r.GetString(4),r.GetString(5),
        r.GetString(6),r.GetString(7),r.GetString(8),r.GetString(9),r.GetString(10),r.IsDBNull(11)?null:r.GetFieldValue<byte[]>(11),r.GetString(12),r.GetInt32(13));
    private static async Task<BookDto?> Get(NpgsqlConnection c,Guid communityId,long id,CancellationToken ct)
    {
        await using var q=Database.Command(c,Select+" WHERE b.community_id=$1 AND b.id=$2",communityId,id);
        await using var r=await q.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?Read(r):null;
    }
    public static void MapBooks(this WebApplication app)
    {
        var g=app.MapGroup("/api/communities/{communityId:guid}/books").RequireAuthorization().RequireRateLimiting("api");
        g.MapGet("",List);g.MapPost("",Create);g.MapGet("/{id:long}",Detail);g.MapPut("/{id:long}",Edit);g.MapPost("/{id:long}/state",State);
        g.MapPost("/{id:long}/conversations",Start);
        g.MapGet("/{id:long}/conversations/{conversationId:long}/messages",Messages);
        g.MapPost("/{id:long}/conversations/{conversationId:long}/messages",Send);
        g.MapPost("/{id:long}/conversations/{conversationId:long}/messages/read",MarkRead);
    }
    private static async Task<IResult> List(Guid communityId,string? audience,string? mode,string? scope,long? before,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)
    {
        if(audience is not(null or "Adult" or "Young" or "Child" or "Baby")||mode is not(null or "Lend" or "Pass")||scope is not(null or "Mine")||before is <=0)return Error("Filtro de Biblioteca no válido.");
        await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        var uid=Tokens.UserId(user);if(await CommunityAccess.Role(c,communityId,uid,ct) is null)return Error("No tienes acceso a esta comunidad.",403);
        await using var q=Database.Command(c,Select+" "+"""
            WHERE b.community_id=$1 AND ($2='' OR b.audience=$2) AND ($3='' OR b.mode=$3) AND b.id<$4
              AND (($5='' AND b.state IN ('Available','Lent')) OR ($5='Mine' AND b.owner_id=$6)) ORDER BY b.id DESC LIMIT 21
            """,communityId,audience??"",mode??"",before??long.MaxValue,scope??"",uid);
        var items=new List<BookDto>();await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))items.Add(Read(r));
        var more=items.Count>20;if(more)items.RemoveAt(20);return Results.Ok(new BookPage(items.ToArray(),more?items[^1].Id:null));
    }
    private static async Task<IResult> Create(Guid communityId,BookInput input,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)
    {
        var request=BookRules.Normalize(input);if(BookRules.Validate(request) is {} error)return Error(error);
        await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);var uid=Tokens.UserId(user);
        if(await CommunityAccess.Role(c,communityId,uid,ct) is null)return Error("No tienes acceso a esta comunidad.",403);
        await Database.Scalar(c,ct,"SELECT pg_advisory_xact_lock(hashtextextended($1,0))",$"book:{communityId}:{uid}:{request.RequestId}");
        var hash=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
        await using(var q=Database.Command(c,"SELECT id,request_hash FROM books WHERE community_id=$1 AND owner_id=$2 AND request_id=$3",communityId,uid,request.RequestId))
        await using(var r=await q.ExecuteReaderAsync(ct))
        {
            if(await r.ReadAsync(ct))
            {
                var id=r.GetInt64(0);var matches=r.GetString(1)==hash;await r.DisposeAsync();
                return matches?Results.Ok(await Get(c,communityId,id,ct)):Error("Este envío ya existe con otros datos.",409);
            }
        }
        var created=(long)(await Database.Scalar(c,ct,"""
            INSERT INTO books(community_id,owner_id,request_id,request_hash,title,author,isbn,condition,audience,mode,description,photo)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12) RETURNING id
            """,communityId,uid,request.RequestId,hash,request.Title,request.Author,request.Isbn,request.Condition,request.Audience,request.Mode,request.Description,request.Photo))!;
        var book=await Get(c,communityId,created,ct);await tx.CommitAsync(ct);return Results.Ok(book);
    }
    private static async Task<IResult> With(Guid communityId,long id,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct,bool write,
        Func<NpgsqlConnection,BookDto,Guid,string,Task<IResult>> action)
    {
        await using var c=await source.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);var uid=Tokens.UserId(user);
        var role=await CommunityAccess.Role(c,communityId,uid,ct);if(role is null)return Error("No tienes acceso a esta comunidad.",403);
        BookDto book;await using(var q=Database.Command(c,Select+" WHERE b.community_id=$1 AND b.id=$2 "+(write?"FOR UPDATE OF b":"FOR SHARE OF b"),communityId,id))
        await using(var r=await q.ExecuteReaderAsync(ct)){if(!await r.ReadAsync(ct))return Error("Libro no disponible.",404);book=Read(r);}
        var result=await action(c,book,uid,role);await tx.CommitAsync(ct);return result;
    }
    private static async Task<MarketConversationDto[]> Chats(NpgsqlConnection c,BookDto book,Guid uid,CancellationToken ct)
    {
        await using var q=Database.Command(c,"""
            SELECT t.id,t.interested_user_id,u.name,(SELECT body FROM market_messages WHERE conversation_id=t.id ORDER BY id DESC LIMIT 1)
            FROM market_conversations t JOIN users u ON u.id=t.interested_user_id WHERE t.book_id=$1 AND ($2 OR t.interested_user_id=$3) ORDER BY t.id DESC
            """,book.Id,book.OwnerId==uid,uid);
        var list=new List<MarketConversationDto>();await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))list.Add(new(r.GetInt64(0),r.GetGuid(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3)));return list.ToArray();
    }
    private static Task<IResult> Detail(Guid communityId,long id,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        With(communityId,id,user,source,ct,false,async(c,b,uid,_)=>Results.Ok(new BookDetail(b,await Chats(c,b,uid,ct))));
    private static Task<IResult> Edit(Guid communityId,long id,int version,BookInput input,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        With(communityId,id,user,source,ct,true,async(c,b,uid,_)=>
        {
            if(b.OwnerId!=uid)return Error("Solo quien publica puede editar el libro.",403);
            if(b.Version!=version||b.State is "Withdrawn" or "Passed")return Error("El libro ha cambiado o ya no se puede editar. Actualiza su ficha.",409);
            var r=BookRules.Normalize(input);if(BookRules.Validate(r) is {} error)return Error(error);
            if(b.State=="Lent"&&r.Mode!=b.Mode)return Error("Marca el libro como devuelto antes de cambiar la modalidad.",409);
            await Database.Execute(c,ct,"""
                UPDATE books SET title=$2,author=$3,isbn=$4,condition=$5,audience=$6,mode=$7,description=$8,photo=$9,version=version+1 WHERE id=$1
                """,id,r.Title,r.Author,r.Isbn,r.Condition,r.Audience,r.Mode,r.Description,r.Photo);return Results.NoContent();
        });
    private static Task<IResult> State(Guid communityId,long id,BookStateRequest request,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        With(communityId,id,user,source,ct,true,async(c,b,uid,role)=>
        {
            if(b.OwnerId!=uid&&(role!="Admin"||request.State!="Withdrawn"))return Error("No puedes cambiar este libro.",403);
            if(request.State is not("Available" or "Lent" or "Passed" or "Withdrawn"))return Error("Estado no válido.");
            if(request.State==b.State)return Results.NoContent();
            if(b.State is "Withdrawn" or "Passed")return Error("Este libro ya está cerrado.",409);
            if((request.State=="Lent"&&b.Mode!="Lend")||(request.State=="Passed"&&b.Mode!="Pass"))return Error("El estado no corresponde a la modalidad.");
            await Database.Execute(c,ct,"UPDATE books SET state=$2,version=version+1 WHERE id=$1",id,request.State);return Results.NoContent();
        });
    private static Task<IResult> Start(Guid communityId,long id,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        With(communityId,id,user,source,ct,true,async(c,b,uid,_)=>
        {
            if(b.OwnerId==uid)return Error("Responde desde tus conversaciones.");
            var old=(await Chats(c,b,uid,ct)).FirstOrDefault();if(old is not null)return Results.Ok(old);
            if(b.State is "Withdrawn" or "Passed")return Error("Este libro ya no admite nuevas solicitudes.",409);
            await Database.Execute(c,ct,"INSERT INTO market_conversations(book_id,interested_user_id) VALUES($1,$2) ON CONFLICT(book_id,interested_user_id) DO NOTHING",id,uid);
            return Results.Ok((await Chats(c,b,uid,ct)).Single());
        });
    private static Task<IResult> Chat(Guid communityId,long id,long conversationId,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct,
        Func<NpgsqlConnection,Guid,Task<IResult>> action)=>With(communityId,id,user,source,ct,false,async(c,b,uid,_)=>
        {
            if(!(bool)(await Database.Scalar(c,ct,"SELECT EXISTS(SELECT 1 FROM market_conversations WHERE id=$1 AND book_id=$2 AND ($3 OR interested_user_id=$4))",conversationId,id,b.OwnerId==uid,uid))!)return Error("Conversación no disponible.",404);
            return await action(c,uid);
        });
    private static Task<IResult> Messages(Guid communityId,long id,long conversationId,long? before,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        Chat(communityId,id,conversationId,user,source,ct,(c,_)=>ConversationMessages.List(c,conversationId,before,ct));
    private static Task<IResult> Send(Guid communityId,long id,long conversationId,MarketMessageRequest request,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        Chat(communityId,id,conversationId,user,source,ct,(c,uid)=>ConversationMessages.Send(c,conversationId,uid,request,ct));
    private static Task<IResult> MarkRead(Guid communityId,long id,long conversationId,MessageReadRequest request,ClaimsPrincipal user,NpgsqlDataSource source,CancellationToken ct)=>
        Chat(communityId,id,conversationId,user,source,ct,(c,uid)=>ConversationMessages.MarkRead(c,conversationId,uid,request,ct));
}
