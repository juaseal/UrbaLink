using System.Security.Claims;
using Npgsql;
using UrbaLink.Contracts;

namespace UrbaLink.Api;

public static class MarketEndpoints
{
    private static IResult Error(string text, int status = 409) => Results.Json(new ApiError(text), statusCode: status);
    private static NpgsqlCommand Q(NpgsqlConnection c, string sql, params object?[] values) =>
        Database.Command(c, sql, values.Select(v => v ?? DBNull.Value).ToArray());
    private static async Task<object?> Scalar(NpgsqlConnection c, string sql, params object?[] values)
    { await using var q = Q(c,sql,values); return await q.ExecuteScalarAsync(); }
    private static async Task Execute(NpgsqlConnection c, string sql, params object?[] values)
    { await using var q = Q(c,sql,values); await q.ExecuteNonQueryAsync(); }

    public static void MapMarket(this WebApplication app)
    {
        var g = app.MapGroup("/api/communities/{communityId:guid}/items").RequireAuthorization().RequireRateLimiting("api");
        g.MapGet("", List);
        g.MapGet("/{id:long}", Detail);
        g.MapPost("", Create);
        g.MapPost("/{id:long}/bids", Bid);
        g.MapPost("/{id:long}/requests", Request);
        g.MapPost("/{id:long}/requests/{requestId:long}", Decide);
        g.MapPost("/{id:long}/withdraw", Withdraw);
        g.MapPost("/{id:long}/complete", CompleteAuction);
    }

    // Writes lock the community before the item. Archiving a community cannot race an accepted operation.
    private static async Task<string?> Access(NpgsqlConnection c, Guid community, Guid user) =>
        await Scalar(c,"""
            SELECT m.role FROM communities c JOIN memberships m ON m.community_id=c.id
            WHERE c.id=$1 AND c.deleted_at IS NULL AND m.user_id=$2 FOR SHARE OF c
            """,community,user) as string;

    private const string SelectItem = """
        SELECT i.id,i.owner_id,u.name,i.title,i.description,i.mode,i.price_cents,i.period,i.ends_at,i.bid_step_cents,
          CASE WHEN i.state<>'Active' THEN i.state
               WHEN i.mode='Auction' AND i.ends_at<=clock_timestamp() THEN 'Ended'
               WHEN i.mode='Sale' AND EXISTS(SELECT 1 FROM market_requests r WHERE r.item_id=i.id AND r.state='Accepted') THEN 'Reserved'
               ELSE 'Active' END,
          b.amount_cents,(SELECT count(*)::int FROM market_bids WHERE item_id=i.id),
          COALESCE(b.user_id=$2,false),
          CASE WHEN i.mode='Auction' AND i.ends_at<=clock_timestamp() THEN winner.name END,i.photo
        FROM market_items i JOIN users u ON u.id=i.owner_id
        LEFT JOIN LATERAL (SELECT amount_cents,user_id FROM market_bids WHERE item_id=i.id ORDER BY amount_cents DESC LIMIT 1) b ON true
        LEFT JOIN users winner ON winner.id=b.user_id
        """;
    private static ItemDto Read(NpgsqlDataReader r) => new(r.GetInt64(0),r.GetGuid(1),r.GetString(2),r.GetString(3),r.GetString(4),
        r.GetString(5),r.GetInt32(6),r.IsDBNull(7)?null:r.GetString(7),r.IsDBNull(8)?null:new DateTimeOffset(r.GetDateTime(8)),
        r.GetInt32(9),r.GetString(10),r.IsDBNull(11)?null:r.GetInt32(11),r.GetInt32(12),r.GetBoolean(13),
        r.IsDBNull(14)?null:r.GetString(14),r.IsDBNull(15)?null:r.GetFieldValue<byte[]>(15));
    private static async Task<ItemDto?> Item(NpgsqlConnection c, Guid community, long id, Guid user)
    {
        await using var q = Q(c,SelectItem+" WHERE i.community_id=$1 AND i.id=$3",community,user,id);
        await using var r = await q.ExecuteReaderAsync();
        return await r.ReadAsync()?Read(r):null;
    }
    private static async Task<IResult> List(Guid communityId, string? mode, string? scope, long? before, ClaimsPrincipal user, NpgsqlDataSource source)
    {
        if (mode is not (null or "Sale" or "Rent" or "Auction") || scope is not (null or "Mine" or "Activity") || before is <=0)
            return Error("Filtro no válido.",400);
        var uid=Tokens.UserId(user);
        await using var c=await source.OpenConnectionAsync();
        if (await Access(c,communityId,uid) is null) return Error("No tienes acceso a esta comunidad.",403);
        await using var q=Q(c,SelectItem+"""
             WHERE i.community_id=$1 AND ($3='' OR i.mode=$3) AND i.id<$4
             AND (($5='' AND i.state='Active' AND (i.mode<>'Auction' OR i.ends_at>clock_timestamp()))
                  OR ($5='Mine' AND i.owner_id=$2)
                  OR ($5='Activity' AND (EXISTS(SELECT 1 FROM market_requests WHERE item_id=i.id AND user_id=$2)
                      OR EXISTS(SELECT 1 FROM market_bids WHERE item_id=i.id AND user_id=$2))))
             ORDER BY i.id DESC LIMIT 21
            """,communityId,uid,mode??"",before??long.MaxValue,scope??"");
        var list=new List<ItemDto>();
        await using var r=await q.ExecuteReaderAsync();
        while(await r.ReadAsync()) list.Add(Read(r));
        var more=list.Count>20;
        if(more) list.RemoveAt(20);
        return Results.Ok(new ItemPage(list.ToArray(),more?list[^1].Id:null));
    }
    private static async Task<IResult> Detail(Guid communityId,long id,ClaimsPrincipal user,NpgsqlDataSource source)
    {
        var uid=Tokens.UserId(user);
        await using var c=await source.OpenConnectionAsync();
        if(await Access(c,communityId,uid) is null) return Error("No tienes acceso a esta comunidad.",403);
        var item=await Item(c,communityId,id,uid);
        if(item is null) return Error("Objeto no disponible.",404);
        var requests=new List<MarketRequestDto>();
        await using(var q=Q(c,"""
            SELECT r.id,r.user_id,u.name,r.starts_at,r.ends_at,r.total_cents,r.note,r.state
            FROM market_requests r JOIN users u ON u.id=r.user_id
            WHERE r.item_id=$1 AND ($2 OR r.user_id=$3) ORDER BY r.id DESC
            """,id,item.OwnerId==uid,uid))
        await using(var r=await q.ExecuteReaderAsync())
            while(await r.ReadAsync()) requests.Add(new(r.GetInt64(0),r.GetGuid(1),r.GetString(2),
                r.IsDBNull(3)?null:new DateTimeOffset(r.GetDateTime(3)),r.IsDBNull(4)?null:new DateTimeOffset(r.GetDateTime(4)),
                r.GetInt64(5),r.GetString(6),r.GetString(7)));
        var slots=new List<BookingSlot>();
        await using(var q=Q(c,"SELECT starts_at,ends_at FROM market_requests WHERE item_id=$1 AND state='Accepted' AND ends_at>clock_timestamp() ORDER BY starts_at",id))
        await using(var r=await q.ExecuteReaderAsync())
            while(await r.ReadAsync()) slots.Add(new(new DateTimeOffset(r.GetDateTime(0)),new DateTimeOffset(r.GetDateTime(1))));
        var now=(DateTime)(await Scalar(c,"SELECT clock_timestamp()"))!;
        return Results.Ok(new ItemDetail(item,requests.ToArray(),slots.ToArray(),new DateTimeOffset(now)));
    }

    private static async Task<IResult> Create(Guid communityId,CreateItemRequest request,ClaimsPrincipal user,NpgsqlDataSource source)
    {
        var title=request.Title?.Trim()??""; var description=request.Description?.Trim()??"";
        var ends=request.EndsAt?.ToUniversalTime();
        if(request.RequestId==Guid.Empty || title.Length is <3 or >120 || description.Length is <1 or >4000 ||
            request.Mode is not ("Sale" or "Rent" or "Auction") || request.PriceCents is <0 or >MarketRules.MaxPriceCents ||
            (request.Mode=="Rent" ? MarketRules.PeriodHours(request.Period)==0 : request.Period is not null) ||
            (request.Mode=="Auction" ? ends is null || request.BidStepCents is <1 or >MarketRules.MaxPriceCents : ends is not null || request.BidStepCents!=0) ||
            (request.Photo is {} photo && (photo.Length is <4 or >MarketRules.MaxPhotoBytes || photo[0]!=255 || photo[1]!=216 || photo[^2]!=255 || photo[^1]!=217)))
            return Error("Revisa título, descripción, precio, modalidad y foto JPEG.",400);
        var uid=Tokens.UserId(user);
        await using var c=await source.OpenConnectionAsync();
        await using var tx=await c.BeginTransactionAsync();
        if(await Access(c,communityId,uid) is null) return Error("No tienes acceso a esta comunidad.",403);
        // Serialize retries before testing the request id.
        await Scalar(c,"SELECT pg_advisory_xact_lock(hashtextextended($1,0))",$"market:{communityId}:{uid}:{request.RequestId}");
        var existing=await Scalar(c,"SELECT id FROM market_items WHERE community_id=$1 AND owner_id=$2 AND request_id=$3",communityId,uid,request.RequestId);
        if(existing is long oldId)
        {
            var old=(await Item(c,communityId,oldId,uid))!;
            return old.Title==title && old.Description==description && old.Mode==request.Mode && old.PriceCents==request.PriceCents &&
                old.Period==request.Period && old.EndsAt==ends && old.BidStepCents==request.BidStepCents &&
                (old.Photo??[]).SequenceEqual(request.Photo??[]) ? Results.Ok(old) : Error("Este envío ya existe con otros datos. Abre un formulario nuevo.");
        }
        var now=new DateTimeOffset((DateTime)(await Scalar(c,"SELECT clock_timestamp()"))!);
        if(ends is {} end && (end<now.AddMinutes(5) || end>now.AddDays(30))) return Error("La subasta debe cerrar entre 5 minutos y 30 días desde ahora.",400);
        var id=(long)(await Scalar(c,"""
            INSERT INTO market_items(community_id,owner_id,request_id,title,description,mode,price_cents,period,ends_at,bid_step_cents,photo)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11) RETURNING id
            """,communityId,uid,request.RequestId,title,description,request.Mode,request.PriceCents,request.Period,ends,request.BidStepCents,request.Photo))!;
        var result=await Item(c,communityId,id,uid);
        await tx.CommitAsync();
        return Results.Ok(result);
    }

    private static async Task<IResult> Mutate(Guid community,long id,ClaimsPrincipal principal,NpgsqlDataSource source,
        Func<NpgsqlConnection,ItemDto,Guid,string,Task<IResult>> action)
    {
        var uid=Tokens.UserId(principal);
        await using var c=await source.OpenConnectionAsync();
        await using var tx=await c.BeginTransactionAsync();
        var role=await Access(c,community,uid);
        if(role is null) return Error("No tienes acceso a esta comunidad.",403);
        if(await Scalar(c,"SELECT id FROM market_items WHERE community_id=$1 AND id=$2 FOR UPDATE",community,id) is null)
            return Error("Objeto no disponible.",404);
        var item=(await Item(c,community,id,uid))!;
        var result=await action(c,item,uid,role);
        await tx.CommitAsync();
        return result;
    }
    private static Task<IResult> Bid(Guid communityId,long id,BidRequest request,ClaimsPrincipal user,NpgsqlDataSource source) =>
        Mutate(communityId,id,user,source,async(c,item,uid,_)=>
        {
            if(request.RequestId==Guid.Empty || request.AmountCents is <1 or >MarketRules.MaxPriceCents) return Error("Importe de puja no válido.",400);
            var old=await Scalar(c,"SELECT amount_cents FROM market_bids WHERE item_id=$1 AND user_id=$2 AND request_id=$3",id,uid,request.RequestId);
            if(old is int amount) return amount==request.AmountCents?Results.NoContent():Error("Este envío ya contiene otra puja.");
            if(item.OwnerId==uid) return Error("No puedes pujar por tu propio objeto.",403);
            if(item.Mode!="Auction" || item.State!="Active") return Error("La subasta ya no admite pujas.");
            var minimum=item.HighestBidCents is {} high ? (long)high+item.BidStepCents : Math.Max(1,item.PriceCents);
            if(request.AmountCents<minimum) return Error($"La puja mínima actual es {minimum/100m:0.00} €.");
            // Recheck the deadline in the insertion, using wall time after all locks and checks.
            var bid=await Scalar(c,"""
                INSERT INTO market_bids(item_id,user_id,request_id,amount_cents)
                SELECT id,$2,$3,$4 FROM market_items WHERE id=$1 AND ends_at>clock_timestamp() RETURNING id
                """,id,uid,request.RequestId,request.AmountCents);
            return bid is null?Error("La subasta ha terminado."):Results.NoContent();
        });

    private static Task<IResult> Request(Guid communityId,long id,ObjectRequest request,ClaimsPrincipal user,NpgsqlDataSource source) =>
        Mutate(communityId,id,user,source,async(c,item,uid,_)=>
        {
            var start=request.StartsAt?.ToUniversalTime(); var end=request.EndsAt?.ToUniversalTime(); var note=request.Note?.Trim()??"";
            if(request.RequestId==Guid.Empty || note.Length>1000) return Error("Solicitud no válida.",400);
            var retry=await Scalar(c,"""
                SELECT (starts_at IS NOT DISTINCT FROM $4::timestamptz AND ends_at IS NOT DISTINCT FROM $5::timestamptz AND note=$6)
                FROM market_requests WHERE item_id=$1 AND user_id=$2 AND request_id=$3
                """,id,uid,request.RequestId,start,end,note);
            if(retry is bool same) return same?Results.NoContent():Error("Este envío ya existe con otros datos.");
            if(item.OwnerId==uid) return Error("Este objeto es tuyo.",403);
            if(item.Mode=="Auction" || item.State!="Active") return Error("Este objeto no admite solicitudes ahora.");
            long total=item.PriceCents;
            if(item.Mode=="Rent")
            {
                var now=new DateTimeOffset((DateTime)(await Scalar(c,"SELECT clock_timestamp()"))!);
                if(start is null || end is null || start<now || end<=start || end>now.AddDays(366) || end-start>TimeSpan.FromDays(90))
                    return Error("Elige fechas futuras y una duración máxima de 90 días, dentro del próximo año.",400);
                total=MarketRules.RentalTotal(item.PriceCents,item.Period!,start.Value,end.Value);
                if(await Overlaps(c,id,start.Value,end.Value)) return Error("El objeto ya está reservado en esas fechas.");
            }
            else if(start is not null || end is not null) return Error("Una compra no necesita fechas de alquiler.",400);
            if((bool)(await Scalar(c,"SELECT EXISTS(SELECT 1 FROM market_requests WHERE item_id=$1 AND user_id=$2 AND state='Pending')",id,uid))!)
                return Error("Ya tienes una solicitud pendiente para este objeto.");
            await Execute(c,"""
                INSERT INTO market_requests(item_id,user_id,request_id,starts_at,ends_at,total_cents,note)
                VALUES($1,$2,$3,$4,$5,$6,$7)
                """,id,uid,request.RequestId,start,end,total,note);
            return Results.NoContent();
        });
    private static async Task<bool> Overlaps(NpgsqlConnection c,long id,DateTimeOffset start,DateTimeOffset end) =>
        (bool)(await Scalar(c,"SELECT EXISTS(SELECT 1 FROM market_requests WHERE item_id=$1 AND state='Accepted' AND starts_at<$3 AND ends_at>$2)",id,start,end))!;

    private static Task<IResult> Decide(Guid communityId,long id,long requestId,RequestDecision request,ClaimsPrincipal user,NpgsqlDataSource source) =>
        Mutate(communityId,id,user,source,async(c,item,uid,_)=>
        {
            Guid requester; string state; DateTimeOffset? start,end;
            await using(var q=Q(c,"SELECT user_id,state,starts_at,ends_at FROM market_requests WHERE item_id=$1 AND id=$2",id,requestId))
            await using(var r=await q.ExecuteReaderAsync())
            {
                if(!await r.ReadAsync()) return Error("Solicitud no disponible.",404);
                requester=r.GetGuid(0); state=r.GetString(1); start=r.IsDBNull(2)?null:new DateTimeOffset(r.GetDateTime(2)); end=r.IsDBNull(3)?null:new DateTimeOffset(r.GetDateTime(3));
            }
            var owner=item.OwnerId==uid;
            if(request.Action=="Cancel" ? requester!=uid : !owner) return Error("No tienes permiso para esta acción.",403);
            var target=request.Action switch { "Accept"=>"Accepted", "Reject"=>"Rejected", "Cancel"=>"Cancelled", "Complete"=>"Completed", _=>"" };
            if(target=="") return Error("Acción no válida.",400);
            if(state==target) return Results.NoContent();
            if(item.State is "Withdrawn" or "Completed") return Error("El objeto ya no está activo.");
            if(request.Action=="Complete" ? state!="Accepted" : state!="Pending") return Error("La solicitud ya ha cambiado. Actualiza el objeto.");
            if(request.Action=="Accept")
            {
                if(item.Mode=="Sale" && item.State!="Active") return Error("La venta ya está reservada.");
                if(start is {} from && end is {} until)
                {
                    var now=new DateTimeOffset((DateTime)(await Scalar(c,"SELECT clock_timestamp()"))!);
                    if(from<=now) return Error("La fecha de inicio ha pasado; pide una nueva solicitud.");
                    if(await Overlaps(c,id,from,until)) return Error("Las fechas coinciden con otra reserva aceptada.");
                }
            }
            await Execute(c,"UPDATE market_requests SET state=$3 WHERE item_id=$1 AND id=$2",id,requestId,target);
            if(request.Action=="Accept")
                await Execute(c,"""
                    UPDATE market_requests SET state='Rejected' WHERE item_id=$1 AND id<>$2 AND state='Pending'
                      AND ($3 OR (starts_at<$5::timestamptz AND ends_at>$4::timestamptz))
                    """,id,requestId,item.Mode=="Sale",start,end);
            if(request.Action=="Complete" && item.Mode=="Sale") await Execute(c,"UPDATE market_items SET state='Completed' WHERE id=$1",id);
            return Results.NoContent();
        });
    private static Task<IResult> Withdraw(Guid communityId,long id,ClaimsPrincipal user,NpgsqlDataSource source) =>
        Mutate(communityId,id,user,source,async(c,item,uid,role)=>
        {
            if(item.OwnerId!=uid && role!="Admin") return Error("No puedes retirar este objeto.",403);
            if(item.State=="Withdrawn") return Results.NoContent();
            if(item.State=="Completed") return Error("La operación ya está completada.");
            if(item.BidCount>0 || (bool)(await Scalar(c,"SELECT EXISTS(SELECT 1 FROM market_requests WHERE item_id=$1 AND state='Accepted')",id))!)
                return Error("No se puede retirar un objeto con pujas o reservas aceptadas.");
            await Execute(c,"UPDATE market_items SET state='Withdrawn' WHERE id=$1",id);
            await Execute(c,"UPDATE market_requests SET state='Rejected' WHERE item_id=$1 AND state='Pending'",id);
            return Results.NoContent();
        });
    private static Task<IResult> CompleteAuction(Guid communityId,long id,ClaimsPrincipal user,NpgsqlDataSource source) =>
        Mutate(communityId,id,user,source,async(c,item,uid,_)=>
        {
            if(item.OwnerId!=uid) return Error("Solo el propietario puede confirmar la entrega.",403);
            if(item.Mode!="Auction") return Error("Esta acción es para una subasta.",400);
            if(item.State=="Completed") return Results.NoContent();
            if(item.State!="Ended" || item.BidCount==0) return Error("La subasta debe haber terminado con una puja ganadora.");
            await Execute(c,"UPDATE market_items SET state='Completed' WHERE id=$1",id);
            return Results.NoContent();
        });
}
