using Npgsql;

namespace UrbaLink.Api;

internal static class CommunityAccess
{
    // Call inside a transaction, before taking locks on posts, items or invitations.
    // SHARE conflicts with archive/restore updates; KEY SHARE would not protect deleted_at.
    public static async Task<string?> Role(NpgsqlConnection connection, Guid communityId, Guid userId, CancellationToken ct = default)
    {
        await using var command = Database.Command(connection, """
            SELECT m.role FROM communities c JOIN memberships m ON m.community_id=c.id
            WHERE c.id=$1 AND c.deleted_at IS NULL AND m.user_id=$2 FOR SHARE OF c
            """, communityId, userId);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    public static async Task<string?> LockActive(NpgsqlConnection connection, Guid communityId, CancellationToken ct)
    {
        await using var command = Database.Command(connection,
            "SELECT name FROM communities WHERE id=$1 AND deleted_at IS NULL FOR SHARE", communityId);
        return await command.ExecuteScalarAsync(ct) as string;
    }
}
