namespace PostNavigator.Wpf;

public sealed record XPost(
    string Id,
    string AuthorHandle,
    string? AuthorDisplayName,
    string Text,
    DateTimeOffset PublishedAtUtc,
    string Url,
    bool IsTextTruncated);
