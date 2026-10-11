namespace UrbaLink.Contracts;

public sealed record MessageReadRequest(long UpToId);
public sealed record MessageNotification(Guid CommunityId, string CommunityName, string Role,
    long ItemId, string ItemTitle, long ConversationId, int UnreadCount, string SenderName, string Preview, string Kind = "Object");
public sealed record NotificationInbox(int UnreadCount, MessageNotification[] Items);
