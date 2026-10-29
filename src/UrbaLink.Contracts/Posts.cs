namespace UrbaLink.Contracts;

public static class PostCategories
{
    public const string HelpGroup = "Ayuda y servicios";
    public static readonly string[] Help = ["Ayuda", "Ofrezco", "Recomendación"];
    public static string Label(string key) => key switch { "Ayuda" => "Necesito ayuda", "Ofrezco" => "Servicios", "Recomendación" => "Recomendaciones", _ => key };
    public static string Key(string label) => label switch { "Necesito ayuda" => "Ayuda", "Servicios" => "Ofrezco", "Recomendaciones" => "Recomendación", _ => label };
    public static readonly string[] All = ["General", "Ayuda", "Ofrezco", "Recomendación", "Material", "Coche", "Aviso oficial"];
}

public sealed record CreatePostRequest(string Title, string Body, string Category, byte[]? Photo = null);
public sealed record PostDto(long Id, Guid CommunityId, Guid AuthorId, string AuthorName,
    string Title, string Body, string Category, DateTimeOffset CreatedAt, byte[]? Photo = null);
public sealed record PostPage(PostDto[] Items, long? NextBefore);
