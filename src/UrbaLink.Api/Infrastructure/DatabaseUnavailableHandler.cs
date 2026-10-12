using Microsoft.AspNetCore.Diagnostics;
using Npgsql;
using UrbaLink.Contracts;

namespace UrbaLink.Api;

public sealed class DatabaseUnavailableHandler(ILogger<DatabaseUnavailableHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (context.RequestAborted.IsCancellationRequested) return false;
        if (exception is not TimeoutException && exception is not NpgsqlException { IsTransient: true }
            && exception is not PostgresException { SqlState: "55P03" or "57014" }) return false;
        logger.LogWarning("Operación de base de datos temporalmente no disponible ({ErrorType}).", exception.GetType().Name);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "2";
        await context.Response.WriteAsJsonAsync(new ApiError("No se pudo confirmar la operación. Comprueba su estado antes de repetirla."), ct);
        return true;
    }
}
