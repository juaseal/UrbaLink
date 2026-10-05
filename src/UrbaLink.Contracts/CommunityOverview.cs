namespace UrbaLink.Contracts;

public sealed record DeleteCommunityRequest(string ConfirmName);
public sealed record CommunityOverview(CommunityDto Community, int MemberCount, int ActivePosts,
    int SharedItems, int Routes, PostDto? Notice, PostDto[] Recent);
