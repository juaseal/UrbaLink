using System.Security.Claims;
using System.Text.Json;
using Npgsql;
using UrbaLink.Contracts;
namespace UrbaLink.Api;
public static class ReportEndpoints
{
    public static void MapReports(this WebApplication app)=>app.MapPost("/api/reports",Create).RequireAuthorization().RequireRateLimiting("api");
    private static IResult Error(string text,int status=400)=>Results.Json(new ApiError(text),statusCode:status);
    private static async Task<IResult> Create(ReportRequest input,ClaimsPrincipal user,NpgsqlDataSource db,CancellationToken ct)
    {
        var reason=input.Reason?.Trim()??"";var uid=Tokens.UserId(user);
        if(input.RequestId==Guid.Empty || reason.Length is <5 or >2000 || input.TargetId<0)return Error("Explica el motivo de la denuncia (5–2000 caracteres).");
        await using var c=await db.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        if(await CommunityAccess.Role(c,input.CommunityId,uid,ct) is null)return Error("No tienes acceso a esta comunidad.",403);
        await Database.Scalar(c,ct,"SELECT pg_advisory_xact_lock(hashtextextended($1,0))",$"report:{uid}:{input.RequestId}");
        await using(var q=Database.Command(c,"SELECT id,community_id,kind,target_id,reason FROM moderation_reports WHERE reporter_id=$1 AND request_id=$2",uid,input.RequestId))
        await using(var r=await q.ExecuteReaderAsync(ct))
        {
            if(await r.ReadAsync(ct))return r.GetGuid(1)==input.CommunityId && r.GetString(2)==input.Kind && r.GetInt64(3)==input.TargetId && r.GetString(4)==reason?Results.Ok(new ReportReceipt(r.GetInt64(0))):Error("Este envío ya existe con otros datos.",409);
        }
        var sql=input.Kind switch
        {
            "Community"=>"SELECT jsonb_build_object('name',name)::text FROM communities WHERE id=$1 AND $2::bigint=0 AND $3::uuid IS NOT NULL",
            "Post"=>"SELECT jsonb_build_object('title',title,'body',body,'authorId',author_id,'photo',encode(photo,'base64'))::text FROM posts WHERE community_id=$1 AND id=$2 AND deleted_at IS NULL AND $3::uuid IS NOT NULL",
            "Comment"=>"SELECT jsonb_build_object('title',p.title,'body',m.body,'authorId',m.author_id,'postId',p.id)::text FROM comments m JOIN posts p ON p.id=m.post_id WHERE p.community_id=$1 AND m.id=$2 AND m.deleted_at IS NULL AND p.deleted_at IS NULL AND p.category<>'Ofrezco' AND $3::uuid IS NOT NULL",
            "Item"=>"SELECT jsonb_build_object('title',title,'body',description,'authorId',owner_id,'photo',encode(photo,'base64'))::text FROM market_items WHERE community_id=$1 AND id=$2 AND state<>'Withdrawn' AND $3::uuid IS NOT NULL",
            "Book"=>"SELECT jsonb_build_object('title',title,'body',description,'authorId',owner_id,'photo',encode(photo,'base64'))::text FROM books WHERE community_id=$1 AND id=$2 AND state<>'Withdrawn' AND $3::uuid IS NOT NULL",
            "Donation"=>"SELECT jsonb_build_object('title',title,'body',description,'size',size,'quantity',quantity,'authorId',owner_id,'photo',encode(photo,'base64'))::text FROM clothing_donations WHERE community_id=$1 AND id=$2 AND state<>'Withdrawn' AND $3::uuid IS NOT NULL",
            "Animal"=>"SELECT jsonb_build_object('title',species||' · '||kind,'body',description,'authorId',author_id,'photo',encode(photo,'base64'))::text FROM animal_notices WHERE community_id=$1 AND id=$2 AND status<>'Withdrawn' AND $3::uuid IS NOT NULL",
            "Message"=>"""
                SELECT jsonb_build_object('body',m.body,'authorId',m.author_id,'sentAt',m.created_at,'conversationId',t.id)::text
                FROM market_messages m JOIN conversation_subjects t ON t.id=m.conversation_id
                WHERE t.community_id=$1 AND m.id=$2
                AND (t.owner_id=$3 OR t.interested_user_id=$3)
                """,
            _=>null
        };
        if(sql is null)return Error("Tipo de denuncia no válido.");
        var snapshot=await Database.Scalar(c,ct,sql,input.CommunityId,input.TargetId,uid);
        if(snapshot is not string text)return Error("Contenido no disponible o sin acceso.",404);
        var id=(long)(await Database.Scalar(c,ct,"""
            INSERT INTO moderation_reports(community_id,reporter_id,request_id,kind,target_id,reason,snapshot)
            VALUES($1,$2,$3,$4,$5,$6,$7::jsonb) RETURNING id
            """,input.CommunityId,uid,input.RequestId,input.Kind,input.TargetId,reason,text))!;
        await tx.CommitAsync(ct);return Results.Ok(new ReportReceipt(id));
    }
}

