namespace UrbaLink.Contracts;

public sealed record RegisterRequest(string Name, string? Email, string Password);
public sealed record LoginRequest(string? Email, string Password);
public sealed record AccountDto(Guid Id, string Name, string Email);
public sealed record UpdateProfileRequest(string? Email);
public sealed record SessionDto(string Token, DateTimeOffset ExpiresAt, AccountDto Account);
public sealed record CommunityDto(Guid Id, string Name, string Role, string State = "Active");
public sealed record CreateCommunityRequest(string Name);
public sealed record JoinRequest(string Code);
public sealed record InvitationDto(string Code, DateTimeOffset ExpiresAt, int MaxUses = 1, Guid Id = default);
public sealed record InvitationSummary(Guid Id, DateTimeOffset ExpiresAt, int MaxUses, int Uses);
public sealed record ApiError(string Message);
