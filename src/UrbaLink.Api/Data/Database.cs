using Npgsql;

namespace UrbaLink.Api;

public static class Database
{
    public static async Task<object?> Scalar(NpgsqlConnection connection, CancellationToken ct, string sql, params object?[] values)
    {
        await using var command = Command(connection, sql, values);
        return await command.ExecuteScalarAsync(ct);
    }

    public static async Task Execute(NpgsqlConnection connection, CancellationToken ct, string sql, params object?[] values)
    {
        await using var command = Command(connection, sql, values);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static readonly (int Version, string Resource)[] Migrations = typeof(Database).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("UrbaLink.Api.Data.") && name.EndsWith(".sql"))
        .Select(name => (int.Parse(name["UrbaLink.Api.Data.".Length..].Split('_')[0]), name))
        .OrderBy(m => m.Item1).ToArray();

    public static async Task<bool> SchemaReady(NpgsqlConnection connection, CancellationToken ct)
    {
        if (!(bool)(await Scalar(connection, ct, "SELECT to_regclass('schema_versions') IS NOT NULL"))!) return false;
        await using var command = Command(connection, "SELECT version FROM schema_versions ORDER BY version");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var versions = new List<int>();
        while (await reader.ReadAsync(ct)) versions.Add(reader.GetInt32(0));
        return versions.SequenceEqual(Migrations.Select(m => m.Version));
    }

    public static async Task MigrateAsync(NpgsqlDataSource source, CancellationToken ct = default)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var limits = Command(connection, "SET LOCAL statement_timeout='120s'; SET LOCAL lock_timeout='10s'"))
            await limits.ExecuteNonQueryAsync(ct);
        await using var migrationLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(827341)", connection, transaction);
        await migrationLock.ExecuteNonQueryAsync(ct);
        await using (var versions = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS schema_versions (version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())",
            connection, transaction))
            await versions.ExecuteNonQueryAsync(ct);
        if (Migrations.Length == 0 || !Migrations.Select(m => m.Version).SequenceEqual(Enumerable.Range(1, Migrations.Length)))
            throw new InvalidOperationException("La secuencia de migraciones es inválida.");
        await using (var newest = Command(connection, "SELECT COALESCE(max(version),0) FROM schema_versions"))
            if ((int)(await newest.ExecuteScalarAsync(ct))! > Migrations.Length)
                throw new InvalidOperationException("La base de datos requiere una versión más reciente de la API.");
        foreach (var (version, resource) in Migrations)
        {
            await using var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM schema_versions WHERE version=$1)", connection, transaction);
            check.Parameters.Add(new NpgsqlParameter { Value = version });
            if ((bool)(await check.ExecuteScalarAsync(ct))!) continue;
            await using var stream = typeof(Database).Assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            await using var command = new NpgsqlCommand(await reader.ReadToEndAsync(), connection, transaction);
            command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params object?[] values)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        return command;
    }
}



