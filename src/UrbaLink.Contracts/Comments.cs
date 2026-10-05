namespace UrbaLink.Contracts;

public sealed record CreateCommentRequest(string Body, Guid RequestId);
public sealed record CommentDto(long Id, long PostId, Guid AuthorId, string AuthorName,
    string Body, DateTimeOffset CreatedAt);
public sealed record CommentPage(PostDto Post, CommentDto[] Items, long? NextBefore);

