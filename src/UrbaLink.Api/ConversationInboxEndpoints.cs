using System.Security.Claims;
using Npgsql;
using UrbaLink.Contracts;

namespace UrbaLink.Api;

public static class ConversationInboxEndpoints
{
    public static void MapConversationInbox(this WebApplication app) =>
        app.MapGet("/api/conversations", List).RequireAuthorization().RequireRateLimiting("api");

    private static async Task<IResult> List(long? before, ClaimsPrincipal user, NpgsqlDataSource source, CancellationToken ct)
    {
        if (before is <= 0) return Results.BadRequest(new ApiError("Página de conversaciones no válida."));
        var uid = Tokens.UserId(user);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var query = Database.Command(connection, """
            SELECT t.id,c.id,c.name,
                t.kind,t.subject_id,t.title,
                CASE WHEN t.owner_id=$1 THEN interested.name ELSE author.name END,
                (SELECT body FROM market_messages WHERE conversation_id=t.id ORDER BY id DESC LIMIT 1),
                (SELECT count(*)::int FROM market_messages m WHERE m.conversation_id=t.id AND m.author_id<>$1 AND m.id>COALESCE(r.last_message_id,0))
            FROM conversation_subjects t
            JOIN communities c ON c.id=t.community_id
            JOIN active_memberships member ON member.community_id=c.id AND member.user_id=$1
            JOIN users interested ON interested.id=t.interested_user_id
            JOIN users author ON author.id=t.owner_id
            LEFT JOIN market_message_reads r ON r.conversation_id=t.id AND r.user_id=$1
            WHERE t.id<$2 AND (t.owner_id=$1 OR t.interested_user_id=$1)
            ORDER BY t.id DESC LIMIT 21
            """, uid, before ?? long.MaxValue);
        var items = new List<ConversationSummary>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(new(reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2),
            reader.GetString(3), reader.GetInt64(4), reader.GetString(5), reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt32(8)));
        var more = items.Count > 20; if (more) items.RemoveAt(20);
        return Results.Ok(new ConversationInbox(items.ToArray(), more ? items[^1].Id : null));
    }
}
