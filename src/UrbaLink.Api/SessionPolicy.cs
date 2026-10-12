namespace UrbaLink.Api;

internal static class SessionPolicy
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(180);
    // Avoid writing to the session row for every request and notification poll.
    public static readonly TimeSpan RenewBelow = Lifetime - TimeSpan.FromDays(1);
}
