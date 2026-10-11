namespace UrbaLink.Contracts;

public sealed record CreateItemRequest(Guid RequestId, string Title, string Description, string Mode,
    int PriceCents, string? Period, DateTimeOffset? EndsAt, int BidStepCents, byte[]? Photo);
public sealed record ItemDto(long Id, Guid OwnerId, string OwnerName, string Title, string Description,
    string Mode, int PriceCents, string? Period, DateTimeOffset? EndsAt, int BidStepCents,
    string State, int? HighestBidCents, int BidCount, bool Leading, string? WinnerName, byte[]? Photo,
    bool IsLent, Guid? WinnerId);
public sealed record ItemPage(ItemDto[] Items, long? NextBefore);
public sealed record ItemDetail(ItemDto Item, MarketConversationDto[] Conversations, DateTimeOffset ServerTime);
public sealed record BidRequest(Guid RequestId, int AmountCents);
public sealed record AvailabilityRequest(bool IsLent);
public sealed record StartMarketConversation(Guid? InterestedUserId = null);
public sealed record MarketConversationDto(long Id, Guid InterestedUserId, string Name, string? LastMessage);
public sealed record MarketMessageRequest(Guid RequestId, string Body);
public sealed record MarketMessageDto(long Id, Guid AuthorId, string Name, string Body, DateTimeOffset CreatedAt);
public sealed record MarketMessagePage(MarketMessageDto[] Items, long? NextBefore);

public static class MarketRules
{
    public const int MaxPriceCents = 100_000_000;
    public const int MaxPhotoBytes = 40_000;
    public static int PeriodHours(string? period) => period switch { "Hour" => 1, "Day" => 24, "Week" => 168, _ => 0 };
}
