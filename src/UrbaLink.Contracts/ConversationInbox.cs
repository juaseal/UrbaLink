namespace UrbaLink.Contracts;

public sealed record ConversationSummary(long Id, Guid CommunityId, string CommunityName,
    string Kind, long SubjectId, string Title, string OtherName, string? LastMessage, int UnreadCount);
public sealed record ConversationInbox(ConversationSummary[] Items, long? NextBefore);
