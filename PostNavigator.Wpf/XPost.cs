namespace PostNavigator.Wpf;

public sealed record XPost(
    string Id,
    string AuthorHandle,
    string? AuthorDisplayName,
    string Text,
    DateTimeOffset PublishedAtUtc,
    string Url,
    bool IsTextTruncated)
{
    public PostAttachment[] Attachments { get; init; } = [];

    internal bool HasSameContent(XPost other) =>
        this == (other with { Attachments = Attachments }) && Attachments.SequenceEqual(other.Attachments);
}

public sealed record PostAttachment(
    int Position, string Kind, string LinkUrl, string? MediaUrl, string? PosterUrl, string? AltText);
