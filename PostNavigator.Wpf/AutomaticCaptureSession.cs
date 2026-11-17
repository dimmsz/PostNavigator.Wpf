namespace PostNavigator.Wpf;

internal sealed class AutomaticCaptureSession(string connectionString)
{
    private readonly Dictionary<string, XPost> savedPosts = new(StringComparer.Ordinal);

    public string ConnectionString { get; } = connectionString;

    public XPost[] PendingPosts(IEnumerable<XPost> posts)
    {
        return posts.Where(post =>
            !savedPosts.TryGetValue(post.Id, out var previous) || previous != post).ToArray();
    }

    public void MarkSaved(IEnumerable<XPost> posts)
    {
        foreach (var post in posts)
        {
            savedPosts[post.Id] = post;
        }
    }
}
