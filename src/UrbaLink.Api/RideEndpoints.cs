using System.Security.Claims;
using System.Globalization;
using Npgsql;
using UrbaLink.Contracts;

namespace UrbaLink.Api;

public static class RideEndpoints
{
    private static IResult Error(string text,int status)=>Results.Json(new ApiError(text),statusCode:status);
    private static NpgsqlCommand Q(NpgsqlConnection c,string sql,params object?[] args)=>Database.Command(c,sql,args.Select(x=>x??DBNull.Value).ToArray());
    private const string Select="""
        SELECT p.id,p.community_id,p.author_id,u.name,p.title,p.body,p.category,p.created_at,
            r.origin,r.destination,r.days,r.departure_minutes,r.return_minutes,r.return_next_day,r.seats,r.price_cents,
            r.allows_detour,r.detour_details,r.meeting_point,r.notes
        FROM ride_details r JOIN posts p ON p.id=r.post_id JOIN users u ON u.id=p.author_id
        """;
    private static RideDto Read(NpgsqlDataReader r)=>new(new(r.GetInt64(0),r.GetGuid(1),r.GetGuid(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),new DateTimeOffset(r.GetDateTime(7))),
        r.GetString(8),r.GetString(9),r.GetInt32(10),r.GetInt32(11),r.IsDBNull(12)?null:r.GetInt32(12),r.GetBoolean(13),r.GetInt32(14),r.GetInt32(15),r.GetBoolean(16),r.GetString(17),r.GetString(18),r.GetString(19));
    private static async Task<bool> Access(NpgsqlConnection c,Guid community,Guid user)
    {
        await using var q=Q(c,"SELECT m.role FROM memberships m JOIN communities c ON c.id=m.community_id WHERE c.id=$1 AND c.deleted_at IS NULL AND m.user_id=$2 FOR SHARE OF c",community,user);
        return await q.ExecuteScalarAsync() is string;
    }
    public static void MapRides(this WebApplication app)
    {
        var g=app.MapGroup("/api/communities/{communityId:guid}/rides").RequireAuthorization().RequireRateLimiting("api");
        g.MapGet("",List);g.MapPost("",Create);
    }
    private static async Task<IResult> List(Guid communityId,int? day,long? before,ClaimsPrincipal user,NpgsqlDataSource source)
    {
        if(day is <0 or >6 || before is <=0)return Error("Filtro no válido.",400);
        await using var c=await source.OpenConnectionAsync();await using var tx=await c.BeginTransactionAsync();
        if(!await Access(c,communityId,Tokens.UserId(user)))return Error("No tienes acceso a esta comunidad.",403);
        await using var q=Q(c,Select+" WHERE p.community_id=$1 AND p.deleted_at IS NULL AND p.id<$2 AND ($3=0 OR (r.days & $3)<>0) ORDER BY p.id DESC LIMIT 21",communityId,before??long.MaxValue,day is {} d?1<<d:0);
        var result=new List<RideDto>();await using var r=await q.ExecuteReaderAsync();while(await r.ReadAsync())result.Add(Read(r));
        var more=result.Count>20;if(more)result.RemoveAt(20);
        return Results.Ok(new RidePageResult(result.ToArray(),more?result[^1].Post.Id:null));
    }
    private static async Task<IResult> Create(Guid communityId,CreateRideRequest input,ClaimsPrincipal user,NpgsqlDataSource source)
    {
        var request=RideRules.Normalize(input);
        if(RideRules.Validate(request) is {} error)return Error(error,400);
        var uid=Tokens.UserId(user);await using var c=await source.OpenConnectionAsync();await using var tx=await c.BeginTransactionAsync();
        if(!await Access(c,communityId,uid))return Error("No tienes acceso a esta comunidad.",403);
        await using(var gate=Q(c,"SELECT pg_advisory_xact_lock(hashtextextended($1,0))",$"ride:{communityId}:{uid}:{request.RequestId}"))await gate.ExecuteScalarAsync();
        await using(var existing=Q(c,Select+" WHERE r.community_id=$1 AND r.author_id=$2 AND r.request_id=$3",communityId,uid,request.RequestId))
        await using(var r=await existing.ExecuteReaderAsync())
        {
            if(await r.ReadAsync())
            {
                var old=Read(r);var original=new CreateRideRequest(request.RequestId,old.Origin,old.Destination,old.Days,old.DepartureMinutes,old.ReturnMinutes,old.ReturnNextDay,old.Seats,old.PriceCents,old.AllowsDetour,old.DetourDetails,old.MeetingPoint,old.Notes);
                return original==request?Results.Ok(old):Error("Este envío ya existe con otros datos. Comprueba tus trayectos antes de publicar de nuevo.",409);
            }
        }
        var price=(request.PriceCents/100m).ToString("C",CultureInfo.GetCultureInfo("es-ES"));
        var body=$"{request.Origin} → {request.Destination}\n{RideRules.DaysText(request.Days)}\nIda: {RideRules.TimeText(request.DepartureMinutes)}";
        body+=request.ReturnMinutes is {} back?$" · Vuelta: {RideRules.TimeText(back)}{(request.ReturnNextDay?" (día siguiente)":"")}":" · Sin vuelta";
        body+=$"\n{request.Seats} plazas · {price} por asiento y sentido\n"+(request.AllowsDetour?"Acepto desvíos: "+request.DetourDetails:"Sin desvíos");
        if(request.MeetingPoint.Length>0)body+="\nEncuentro: "+request.MeetingPoint;
        if(request.Notes.Length>0)body+="\n"+request.Notes;
        var title=$"{request.Origin} → {request.Destination}";if(title.Length>120)title=title[..117]+"…";
        long id;
        await using(var post=Q(c,"INSERT INTO posts(community_id,author_id,title,body,category) VALUES($1,$2,$3,$4,'Coche') RETURNING id",communityId,uid,title,body))id=(long)(await post.ExecuteScalarAsync())!;
        await using(var insert=Q(c,"""
            INSERT INTO ride_details(post_id,community_id,author_id,request_id,origin,destination,days,departure_minutes,return_minutes,return_next_day,seats,price_cents,allows_detour,detour_details,meeting_point,notes)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16)
            """,id,communityId,uid,request.RequestId,request.Origin,request.Destination,request.Days,request.DepartureMinutes,request.ReturnMinutes,request.ReturnNextDay,request.Seats,request.PriceCents,request.AllowsDetour,request.DetourDetails,request.MeetingPoint,request.Notes))await insert.ExecuteNonQueryAsync();
        RideDto result;
        await using(var query=Q(c,Select+" WHERE p.id=$1",id))await using(var r=await query.ExecuteReaderAsync()){await r.ReadAsync();result=Read(r);}
        await tx.CommitAsync();return Results.Ok(result);
    }
}
