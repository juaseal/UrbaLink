using UrbaLink.Contracts;

namespace UrbaLink.Api;

// Bound in-flight API work before authentication borrows a database connection.
// This is per instance; a deployment with several replicas also needs an edge limit.
public sealed class RequestAdmission : IMiddleware, IDisposable
{
    private readonly SemaphoreSlim slots;

    public RequestAdmission(IConfiguration configuration)
    {
        var capacity = configuration.GetValue("Availability:MaxConcurrentRequests", 64);
        if (capacity is < 1 or > 1000) throw new InvalidOperationException("MaxConcurrentRequests debe estar entre 1 y 1000.");
        slots = new SemaphoreSlim(capacity, capacity);
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments("/api")) { await next(context); return; }
        if (!await slots.WaitAsync(0, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "2";
            await context.Response.WriteAsJsonAsync(new ApiError("El servidor está ocupado. Inténtalo de nuevo en unos segundos."), context.RequestAborted);
            return;
        }
        try { await next(context); }
        finally { slots.Release(); }
    }

    public void Dispose() => slots.Dispose();
}
