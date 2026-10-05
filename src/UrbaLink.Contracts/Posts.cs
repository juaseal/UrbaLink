namespace UrbaLink.Contracts;

public static class PostCategories
{
    public static readonly string[] All = ["General", "Ayuda", "Ofrezco", "Material", "Coche", "Aviso oficial"];
}

public sealed record CreatePostRequest(string Title, string Body, string Category);
public sealed record PostDto(long Id, Guid CommunityId, Guid AuthorId, string AuthorName,
    string Title, string Body, string Category, DateTimeOffset CreatedAt);
public sealed record PostPage(PostDto[] Items, long? NextBefore);
