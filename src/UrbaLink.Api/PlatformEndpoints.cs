using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using UrbaLink.Contracts;
namespace UrbaLink.Api;

public static class PlatformEndpoints
{
    public sealed record LoginInput(string? Email,string Password,string? Username=null);
    public sealed record Decision(string State,string Reason);
    private static readonly PasswordHasher<string> Hasher=new();
    private static readonly string DummyHash=Hasher.HashPassword("",Tokens.New());
    private static IResult Error(string message,int code=400)=>Results.Json(new ApiError(message),statusCode:code);
    internal static bool IsAdmin(IConfiguration config,Guid id)=>(config["PlatformAdmin:UserIds"]??"").Split(',',StringSplitOptions.TrimEntries).Any(x=>Guid.TryParse(x,out var configured)&&configured==id);
    private static Task Audit(NpgsqlConnection c,Guid actor,string action,string target,string reason,CancellationToken ct)=>Database.Execute(c,ct,"INSERT INTO platform_audit(actor_id,action,target,reason) VALUES($1,$2,$3,$4)",actor,action,target,reason);
    public static void MapPlatform(this WebApplication app)
    {
        var g=app.MapGroup("/api/platform").RequireRateLimiting("api");
        g.MapPost("/session",Login).RequireRateLimiting("auth");
        g.MapDelete("/session",(HttpContext h,NpgsqlDataSource db,IConfiguration config,CancellationToken ct)=>With(h,db,config,ct,async(c,uid)=>
        {await Database.Execute(c,ct,"DELETE FROM platform_sessions WHERE token_hash=$1",Tokens.Hash(h.Request.Headers.Authorization.ToString()[7..]));return Results.NoContent();}));
        g.MapGet("/communities",Communities);
        g.MapGet("/communities/{id:guid}/users",Users);
        g.MapGet("/communities/{id:guid}/content",Content);
        g.MapPost("/communities/{id:guid}/state",CommunityState);
        g.MapPost("/communities/{id:guid}/users/{userId:guid}/state",MemberState);
        g.MapPost("/users/{id:guid}/state",UserState);
        g.MapPost("/communities/{id:guid}/content/{kind}/{target:long}/remove",Remove);
        g.MapGet("/reports",Reports);
        g.MapGet("/reports/{id:long}",ReportDetail);
        g.MapPost("/reports/{id:long}/resolve",Resolve);
        g.MapGet("/audit",History);
    }
    private static async Task<IResult> Login(LoginInput input,NpgsqlDataSource db,IConfiguration config,CancellationToken ct)
    {
        var email=input.Username is not null?input.Username.Trim():input.Email?.Trim().ToLowerInvariant()??"";
        if(email.Length>254 || input.Password is null || input.Password.Length is <1 or >128)return Error("Acceso no válido.",401);
        await using var c=await db.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        Guid? uid=null;var hash=DummyHash;
        await using(var q=Database.Command(c,"SELECT id,password_hash FROM users WHERE (CASE WHEN $2 THEN lower(username)=lower($1) ELSE email=$1 END) AND NOT is_suspended FOR SHARE",email,input.Username is not null))
        await using(var r=await q.ExecuteReaderAsync(ct)){if(await r.ReadAsync(ct)){uid=r.GetGuid(0);hash=r.GetString(1);}}
        var password=Hasher.VerifyHashedPassword(email,hash,input.Password);
        if(uid is not {} id || password==PasswordVerificationResult.Failed || !IsAdmin(config,id))return Error("Acceso no válido. Comprueba credenciales y autorización.",401);
        var token=Tokens.New();var expiry=DateTimeOffset.UtcNow.AddMinutes(30);
        await Database.Execute(c,ct,"DELETE FROM platform_sessions WHERE expires_at<now()");
        await Database.Execute(c,ct,"INSERT INTO platform_sessions(token_hash,user_id,expires_at) VALUES($1,$2,$3)",Tokens.Hash(token),id,expiry);
        await Audit(c,id,"Login","platform","",ct);await tx.CommitAsync(ct);
        return Results.Ok(new{token,expiresAt=expiry});
    }
    private static async Task<IResult> With(HttpContext h,NpgsqlDataSource db,IConfiguration config,CancellationToken ct,Func<NpgsqlConnection,Guid,Task<IResult>> action)
    {
        h.Response.Headers.CacheControl="no-store";
        var header=h.Request.Headers.Authorization.ToString();if(!header.StartsWith("Bearer ") || header.Length!=71)return Error("Inicia sesión en el panel.",401);
        await using var c=await db.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        var user=await Database.Scalar(c,ct,"SELECT s.user_id FROM platform_sessions s JOIN users u ON u.id=s.user_id WHERE s.token_hash=$1 AND s.expires_at>clock_timestamp() AND NOT u.is_suspended FOR SHARE OF s,u",Tokens.Hash(header[7..]));
        if(user is not Guid uid || !IsAdmin(config,uid))return Error("Sesión administrativa no válida.",401);
        var result=await action(c,uid);await tx.CommitAsync(ct);return result;
    }
    private static async Task<IResult> Page(NpgsqlConnection c,string sql,int? offset,CancellationToken ct,params object?[] args)
    {
        var start=offset??0;if(start<0 || start>1_000_000)return Error("Página no válida.");
        // SQL fragments are compile-time constants; only parameters contain client input.
        await using var q=Database.Command(c,"SELECT row_to_json(x)::text FROM ("+sql+$" LIMIT 51 OFFSET {start}) x",args);
        var items=new List<JsonElement>();await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)){using var doc=JsonDocument.Parse(r.GetString(0));items.Add(doc.RootElement.Clone());}
        var more=items.Count>50;if(more)items.RemoveAt(50);return Results.Ok(new{items,nextOffset=more?(int?)(start+50):null});
    }
    private static Task<IResult> Communities(int? offset,string? search,HttpContext h,NpgsqlDataSource db,IConfiguration config,CancellationToken ct)=>With(h,db,config,ct,async(c,uid)=>
    {
        if(search?.Length>120)return Error("Búsqueda demasiado larga.");
        await Audit(c,uid,"ListCommunities","platform","",ct);
        return await Page(c,"""
            SELECT c.id,c.name,c.platform_state,c.deleted_at,c.created_at,
            (SELECT count(*) FROM memberships m WHERE m.community_id=c.id) members,
            (SELECT u.name FROM memberships m JOIN users u ON u.id=m.user_id WHERE m.community_id=c.id ORDER BY m.joined_at,m.user_id LIMIT 1) creator
            FROM communities c WHERE c.name ILIKE '%'||$1||'%' ORDER BY c.created_at DESC,c.id DESC
            """,offset,ct,search??"");
    });
    private static Task<IResult> Users(Guid id,int? offset,HttpContext h,NpgsqlDataSource db,IConfiguration config,CancellationToken ct)=>With(h,db,config,ct,async(c,uid)=>
    {
        await Audit(c,uid,"ListMembers",id.ToString(),"",ct);
        return await Page(c,"SELECT u.id,u.name,u.email,u.is_suspended,m.role,m.is_blocked,m.joined_at FROM memberships m JOIN users u ON u.id=m.user_id WHERE m.community_id=$1 ORDER BY m.joined_at,u.id",offset,ct,id);
    });
    private static Task<IResult> Content(Guid id,int? offset,HttpContext h,NpgsqlDataSource db,IConfiguration config,CancellationToken ct)=>With(h,db,config,ct,async(c,uid)=>
    {
        await Audit(c,uid,"ListPublicContent",id.ToString(),"",ct);
        return await Page(c,"""
            SELECT * FROM (
            SELECT 'Post' kind,id,title,body,author_id,created_at,deleted_at IS NOT NULL removed,NULL::text photo FROM posts WHERE community_id=$1
            UNION ALL SELECT 'Comment',m.id,'Comentario en '||p.title,m.body,m.author_id,m.created_at,m.deleted_at IS NOT NULL,NULL::text FROM comments m JOIN posts p ON p.id=m.post_id WHERE p.community_id=$1 AND p.category<>'Ofrezco'
            UNION ALL SELECT 'Item',id,title,description,owner_id,created_at,state='Withdrawn',encode(photo,'base64') FROM market_items WHERE community_id=$1
            UNION ALL SELECT 'Animal',id,species||' · '||kind,description||' · '||location,author_id,created_at,status='Withdrawn',encode(photo,'base64') FROM animal_notices WHERE community_id=$1
            ) content ORDER BY created_at DESC,kind,id DESC
            """,offset,ct,id);
    });
    private static bool ValidReason(Decision d)=>d.Reason?.Trim().Length is >=5 and <=2000;
    private static Task<IResult> CommunityState(Guid id,Decision input,HttpContext h,NpgsqlDataSource db,IConfiguration config,CancellationToken ct)=>With(h,db,config,ct,async(c,uid)=>
    {
        if(!ValidReason(input) || input.State is not ("Active" or "Suspended"))return Error("Indica un estado y un motivo de 5–2000 caracteres.");
        var previous=await Database.Scalar(c,ct,"SELECT platform_state FROM communities WHERE id=$1 FOR UPDATE",id);if(previous is null)return Error("Comunidad no encontrada.",404);
        await Database.Execute(c,ct,"UPDATE communities SET platform_state=$2 WHERE id=$1",id,input.State);
        if(input.State=="Suspended")await Database.Execute(c,ct,"UPDATE invitations SET expires_at=LEAST(expires_at,now()) WHERE community_id=$1",id);
        await Audit(c,uid,$"Community:{previous}->{input.State}",id.ToString(),input.Reason.Trim(),ct);return Results.NoContent();
    });
    private static Task<IResult> MemberState(Guid id,Guid userId,Decision input,HttpContext h,NpgsqlDataSource db,IConfiguration config,CancellationToken ct)=>With(h,db,config,ct,async(c,uid)=>
    {
        if(!ValidReason(input)||input.State is not ("Blocked" or "Active"))return Error("Indica un motivo y un estado válidos.");
        if(IsAdmin(config,userId))return Error("La cuenta de administración global se gestiona desde la configuración del servidor.",409);
        await Database.Scalar(c,ct,"SELECT id FROM communities WHERE id=$1 FOR UPDATE",id);
        var role=await Database.Scalar(c,ct,"SELECT role FROM memberships WHERE community_id=$1 AND user_id=$2 FOR UPDATE",id,userId);
        if(role is null)return Error("Miembro no encontrado.",404);
        if(input.State=="Blocked" && (string)role=="Admin" && (long)(await Database.Scalar(c,ct,"SELECT count(*) FROM memberships m JOIN users u ON u.id=m.user_id WHERE m.community_id=$1 AND m.role='Admin' AND NOT m.is_blocked AND NOT u.is_suspended AND m.user_id<>$2",id,userId))! ==0)return Error("No puedes bloquear al último administrador activo. Puedes suspender la comunidad.",409);
        await Database.Execute(c,ct,"UPDATE memberships SET is_blocked=$3 WHERE community_id=$1 AND user_id=$2",id,userId,input.State=="Blocked");
        await Audit(c,uid,"Member:"+input.State,$"{id}/{userId}",input.Reason.Trim(),ct);return Results.NoContent();
    });
    private static Task<IResult> UserState(Guid id,Decision input,HttpContext h,NpgsqlDataSource db,IConfiguration config,CancellationToken ct)=>With(h,db,config,ct,async(c,uid)=>
    {
        if(!ValidReason(input)||input.State is not ("Suspended" or "Active"))return Error("Indica un motivo y un estado válidos.");
        if(IsAdmin(config,id))return Error("No puedes suspender una cuenta de administración global desde el panel.",409);
        var found=await Database.Scalar(c,ct,"UPDATE users SET is_suspended=$2 WHERE id=$1 RETURNING id",id,input.State=="Suspended");if(found is null)return Error("Cuenta no encontrada.",404);
        if(input.State=="Suspended")await Database.Execute(c,ct,"DELETE FROM sessions WHERE user_id=$1",id);
        await Audit(c,uid,"User:"+input.State,id.ToString(),input.Reason.Trim(),ct);return Results.NoContent();
    });
    private static Task<IResult> Remove(Guid id,string kind,long target,Decision input,HttpContext h,NpgsqlDataSource db,IConfiguration config,CancellationToken ct)=>With(h,db,config,ct,async(c,uid)=>
    {
        if(!ValidReason(input))return Error("Indica el motivo de la retirada.");
        await Database.Scalar(c,ct,"SELECT id FROM communities WHERE id=$1 FOR UPDATE",id);
        var sql=kind switch
        {
            "Post"=>"UPDATE posts SET deleted_at=COALESCE(deleted_at,now()),deleted_by=$3 WHERE community_id=$1 AND id=$2 RETURNING id",
            "Comment"=>"UPDATE comments m SET deleted_at=COALESCE(m.deleted_at,now()),deleted_by=$3 FROM posts p WHERE p.id=m.post_id AND p.community_id=$1 AND m.id=$2 RETURNING m.id",
            "Item"=>"UPDATE market_items SET state='Withdrawn' WHERE community_id=$1 AND id=$2 AND $3::uuid IS NOT NULL RETURNING id",
            "Animal"=>"UPDATE animal_notices SET status='Withdrawn',closed_at=COALESCE(closed_at,now()),updated_at=now(),version=version+1 WHERE community_id=$1 AND id=$2 AND $3::uuid IS NOT NULL RETURNING id",
            _=>null
        };
        if(sql is null)return Error("Tipo de contenido no válido.");
        if(await Database.Scalar(c,ct,sql,id,target,uid) is null)return Error("Contenido no encontrado.",404);
        await Audit(c,uid,"Remove:"+kind,$"{id}/{target}",input.Reason.Trim(),ct);return Results.NoContent();
    });
    private static Task<IResult> Reports(int? offset,string? status,HttpContext h,NpgsqlDataSource db,IConfiguration config,CancellationToken ct)=>With(h,db,config,ct,(c,uid)=>Page(c,"SELECT id,community_id,kind,target_id,status,created_at FROM moderation_reports WHERE status=$1 ORDER BY id DESC",offset,ct,status??"Open"));
    private static Task<IResult> ReportDetail(long id,HttpContext h,NpgsqlDataSource db,IConfiguration config,CancellationToken ct)=>With(h,db,config,ct,async(c,uid)=>
    {
        var json=await Database.Scalar(c,ct,"""
            SELECT row_to_json(detail)::text FROM (
                SELECT r.*,c.name community_name,u.name reporter_name,a.name author_name
                FROM moderation_reports r JOIN communities c ON c.id=r.community_id
                JOIN users u ON u.id=r.reporter_id
                LEFT JOIN users a ON a.id::text=r.snapshot->>'authorId'
                WHERE r.id=$1
            ) detail
            """,id);if(json is not string text)return Error("Denuncia no encontrada.",404);
        await Audit(c,uid,"ReadReport",id.ToString(),"",ct);using var doc=JsonDocument.Parse(text);return Results.Ok(doc.RootElement.Clone());
    });
    private static Task<IResult> Resolve(long id,Decision input,HttpContext h,NpgsqlDataSource db,IConfiguration config,CancellationToken ct)=>With(h,db,config,ct,async(c,uid)=>
    {
        if(!ValidReason(input)||input.State is not ("Resolved" or "Dismissed"))return Error("Indica la decisión y su motivo.");
        if(await Database.Scalar(c,ct,"UPDATE moderation_reports SET status=$2,resolution=$3,reviewed_by=$4,reviewed_at=now() WHERE id=$1 AND status='Open' RETURNING id",id,input.State,input.Reason.Trim(),uid) is null)return Error("La denuncia ya fue revisada o no existe.",409);
        await Audit(c,uid,"Report:"+input.State,id.ToString(),input.Reason.Trim(),ct);return Results.NoContent();
    });
    private static Task<IResult> History(int? offset,HttpContext h,NpgsqlDataSource db,IConfiguration config,CancellationToken ct)=>With(h,db,config,ct,(c,uid)=>Page(c,"SELECT a.id,u.name actor,a.action,a.target,a.reason,a.created_at FROM platform_audit a JOIN users u ON u.id=a.actor_id ORDER BY a.id DESC",offset,ct));
}


