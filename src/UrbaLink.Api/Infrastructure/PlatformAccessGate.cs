using Npgsql;
using UrbaLink.Contracts;
namespace UrbaLink.Api;
public sealed class PlatformAccessGate(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var parts=(context.Request.Path.Value??"").Split('/',StringSplitOptions.RemoveEmptyEntries);
        if(context.User.Identity?.IsAuthenticated==true && parts.Length>=3 && parts[0].Equals("api",StringComparison.OrdinalIgnoreCase) && parts[1].Equals("communities",StringComparison.OrdinalIgnoreCase) && Guid.TryParse(parts[2],out var community))
        {
            var source=context.RequestServices.GetRequiredService<NpgsqlDataSource>();
            await using var c=await source.OpenConnectionAsync(context.RequestAborted);
            var blocked=(bool)(await Database.Scalar(c,context.RequestAborted,"""
                SELECT EXISTS(SELECT 1 FROM communities c WHERE c.id=$1
                AND (c.platform_state<>'Active' OR EXISTS(SELECT 1 FROM memberships m
                WHERE m.community_id=c.id AND m.user_id=$2 AND m.is_blocked)))
                """,community,Tokens.UserId(context.User)))!;
            if(blocked)
            {
                context.Response.StatusCode=403;
                await context.Response.WriteAsJsonAsync(new ApiError("Esta comunidad está pendiente de aprobación, suspendida o no tienes acceso."));return;
            }
        }
        await next(context);
    }
}


