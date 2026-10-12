using Npgsql;
using UrbaLink.Contracts;

namespace UrbaLink.Api;

internal static class PostRecord
{
    // Shared projection: id, community_id, author_id, author name, title, body, category, created_at.
    public static PostDto Read(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetString(6), new DateTimeOffset(reader.GetDateTime(7)));
}
