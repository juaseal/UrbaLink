using Npgsql;

namespace UrbaLink.Api;

public sealed class ExpiredSessionCleanup(NpgsqlDataSource source, ILogger<ExpiredSessionCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // Bound maintenance work, allowing several instances to clean disjoint batches.
                await using var command = source.CreateCommand("""
                    DELETE FROM sessions WHERE token_hash IN (
                        SELECT token_hash FROM sessions WHERE expires_at <= now()
                        ORDER BY expires_at LIMIT 1000 FOR UPDATE SKIP LOCKED
                    )
                    """);
                await command.ExecuteNonQueryAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) when (e is NpgsqlException or TimeoutException)
            {
                logger.LogWarning("Limpieza de sesiones aplazada ({ErrorType}).", e.GetType().Name);
            }
        }
    }
}
