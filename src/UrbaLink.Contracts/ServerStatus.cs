namespace UrbaLink.Contracts;

public sealed record ServerStatus(string Service, string Status, DateTimeOffset CheckedAt);

public static class ServerAddress
{
    public static bool TryParse(string? value, bool allowHttp, out Uri? address)
    {
        address = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(allowHttp && uri.Scheme == Uri.UriSchemeHttp)) ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath != "/")
            return false;
        address = uri;
        return true;
    }
}
