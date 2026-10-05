namespace UrbaLink.Contracts;

public sealed record CreateItemRequest(Guid RequestId, string Title, string Description, string Mode,
    int PriceCents, string? Period, DateTimeOffset? EndsAt, int BidStepCents, byte[]? Photo);
public sealed record ItemDto(long Id, Guid OwnerId, string OwnerName, string Title, string Description,
    string Mode, int PriceCents, string? Period, DateTimeOffset? EndsAt, int BidStepCents,
    string State, int? HighestBidCents, int BidCount, bool Leading, string? WinnerName, byte[]? Photo);
public sealed record ItemPage(ItemDto[] Items, long? NextBefore);
public sealed record ItemDetail(ItemDto Item, MarketRequestDto[] Requests, BookingSlot[] Bookings,
    DateTimeOffset ServerTime);
public sealed record BookingSlot(DateTimeOffset StartsAt, DateTimeOffset EndsAt);
public sealed record BidRequest(Guid RequestId, int AmountCents);
public sealed record ObjectRequest(Guid RequestId, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string Note);
public sealed record MarketRequestDto(long Id, Guid UserId, string Name, DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt, long TotalCents, string Note, string State);
public sealed record RequestDecision(string Action);

public static class MarketRules
{
    public const int MaxPriceCents = 100_000_000;
    public const int MaxPhotoBytes = 40_000;
    public static int PeriodHours(string? period) => period switch { "Hour" => 1, "Day" => 24, "Week" => 168, _ => 0 };
    public static long RentalTotal(int priceCents, string period, DateTimeOffset start, DateTimeOffset end) =>
        checked((long)Math.Ceiling((end-start).TotalHours / PeriodHours(period)) * priceCents);
}
