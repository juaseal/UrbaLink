namespace UrbaLink.Contracts;

public sealed record CommunitySearchResult(string Kind,long Id,string Title,string Description,
    string AuthorName,DateTimeOffset CreatedAt,string Category);
public sealed record CommunitySearchPage(CommunitySearchResult[] Items,string? NextBefore);
