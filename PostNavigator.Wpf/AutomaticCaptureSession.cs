namespace PostNavigator.Wpf;

internal sealed class AutomaticCaptureSession(string connectionString)
{
    private readonly Dictionary<string, XPost> savedPosts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XPost> observedPosts = new(StringComparer.Ordinal);

    public string ConnectionString { get; } = connectionString;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public DateTime RetryAfterUtc { get; set; }
    public int ObservedCount => observedPosts.Count;
    public int SavedCount => savedPosts.Count;

    public XPost[] PendingPosts(IEnumerable<XPost> posts)
    {
        Observe(posts);
        return observedPosts.Values.Where(post =>
            !savedPosts.TryGetValue(post.Id, out var previous) || previous != post).ToArray();
    }

    public void Observe(IEnumerable<XPost> posts)
    {
        foreach (var post in posts)
        {
            if (!observedPosts.TryGetValue(post.Id, out var previous))
            {
                observedPosts[post.Id] = post;
                continue;
            }

            // Preserve the best text when a virtualized card returns in a shorter form.
            var replaceText = post.Text.Length > 0 &&
                (previous.Text.Length == 0 || !post.IsTextTruncated ||
                    previous.IsTextTruncated && post.Text.Length > previous.Text.Length);
            observedPosts[post.Id] = post with
            {
                Text = replaceText ? post.Text : previous.Text,
                IsTextTruncated = replaceText ? post.IsTextTruncated : previous.IsTextTruncated,
                AuthorDisplayName = string.IsNullOrEmpty(post.AuthorDisplayName)
                    ? previous.AuthorDisplayName : post.AuthorDisplayName
            };
        }
    }

    public void MarkSaved(IEnumerable<XPost> posts)
    {
        foreach (var post in posts)
        {
            savedPosts[post.Id] = post;
        }
    }
}
