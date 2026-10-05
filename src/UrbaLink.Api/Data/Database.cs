using Npgsql;

namespace UrbaLink.Api;

public static class Database
{
    public static async Task MigrateAsync(NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var migrationLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(827341)", connection, transaction);
        await migrationLock.ExecuteNonQueryAsync();
        await using (var versions = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS schema_versions (version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())",
            connection, transaction))
            await versions.ExecuteNonQueryAsync();
        foreach (var (version, file) in new[] { (1, "001_accounts.sql"), (2, "002_posts.sql"), (3, "003_comments.sql"), (4, "004_community_trash.sql"), (5, "005_market.sql") })
        {
            await using var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM schema_versions WHERE version=$1)", connection, transaction);
            check.Parameters.Add(new NpgsqlParameter { Value = version });
            if ((bool)(await check.ExecuteScalarAsync())!) continue;
            await using var stream = typeof(Database).Assembly.GetManifestResourceStream($"UrbaLink.Api.Data.{file}")!;
            using var reader = new StreamReader(stream);
            await using var command = new NpgsqlCommand(await reader.ReadToEndAsync(), connection, transaction);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params object[] values)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        return command;
    }
}



