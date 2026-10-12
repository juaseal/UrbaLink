using Npgsql;
using UrbaLink.Contracts;

namespace UrbaLink.Api;

// Subject adapters authorize the participant before calling this shared store.
// Keep the existing DTOs/table names to preserve compatibility with installed clients.
internal static class ConversationMessages
{
    private static IResult Error(string message, int status) => Results.Json(new ApiError(message), statusCode: status);

    public static async Task<IResult> List(NpgsqlConnection connection, long conversationId, long? before, CancellationToken ct)
    {
        if (before is <= 0) return Error("Cursor no válido.", 400);
        await using var command = Database.Command(connection, """
            SELECT m.id,m.author_id,u.name,m.body,m.created_at
            FROM market_messages m JOIN users u ON u.id=m.author_id
            WHERE m.conversation_id=$1 AND m.id<$2 ORDER BY m.id DESC LIMIT 21
            """, conversationId, before ?? long.MaxValue);
        var messages = new List<MarketMessageDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            messages.Add(new(reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), new DateTimeOffset(reader.GetDateTime(4))));
        var more = messages.Count > 20;
        if (more) messages.RemoveAt(20);
        return Results.Ok(new MarketMessagePage(messages.ToArray(), more ? messages[^1].Id : null));
    }

    public static async Task<IResult> Send(NpgsqlConnection connection, long conversationId, Guid userId, MarketMessageRequest request, CancellationToken ct)
    {
        var body = request.Body?.Trim() ?? "";
        if (request.RequestId == Guid.Empty || body.Length is < 1 or > 2000)
            return Error("Escribe un mensaje de 1 a 2000 caracteres.", 400);
        // Serialize sends only within this conversation. This also keeps message IDs in
        // commit order so a read watermark cannot swallow a later-committing smaller ID.
        await using (var gate = Database.Command(connection, "SELECT id FROM market_conversations WHERE id=$1 FOR UPDATE", conversationId))
            await gate.ExecuteScalarAsync(ct);
        await using (var previous = Database.Command(connection,
            "SELECT body FROM market_messages WHERE conversation_id=$1 AND author_id=$2 AND request_id=$3", conversationId, userId, request.RequestId))
        {
            if (await previous.ExecuteScalarAsync(ct) is string old)
                return old == body ? Results.NoContent() : Error("Este envío ya contiene otro mensaje.", 409);
        }
        await using var insert = Database.Command(connection,
            "INSERT INTO market_messages(conversation_id,author_id,request_id,body) VALUES($1,$2,$3,$4)", conversationId, userId, request.RequestId, body);
        await insert.ExecuteNonQueryAsync(ct);
        return Results.NoContent();
    }

    public static async Task<IResult> MarkRead(NpgsqlConnection connection, long conversationId, Guid userId, MessageReadRequest request, CancellationToken ct)
    {
        await using (var check = Database.Command(connection,
            "SELECT EXISTS(SELECT 1 FROM market_messages WHERE conversation_id=$1 AND id=$2)", conversationId, request.UpToId))
            if (!(bool)(await check.ExecuteScalarAsync(ct))!) return Error("Mensaje no disponible.", 400);
        await using var update = Database.Command(connection, """
            INSERT INTO market_message_reads(conversation_id,user_id,last_message_id) VALUES($1,$2,$3)
            ON CONFLICT(conversation_id,user_id) DO UPDATE
            SET last_message_id=GREATEST(market_message_reads.last_message_id,EXCLUDED.last_message_id)
            """, conversationId, userId, request.UpToId);
        await update.ExecuteNonQueryAsync(ct);
        return Results.NoContent();
    }
}
