using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PostNavigator.Wpf;

internal sealed partial class PostMediaArchive(string directory, HttpClient? client = null)
{
    private static readonly HttpClient DownloadClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly SemaphoreSlim ArchiveWrite = new(1, 1);
    private readonly HttpClient http = client ?? DownloadClient;

    public static PostAttachment[] Merge(IEnumerable<PostAttachment> previous, IEnumerable<PostAttachment> current)
    {
        var attachments = new Dictionary<(string Kind, int Position), PostAttachment>();
        foreach (var item in previous.Concat(current))
        {
            var key = (item.Kind, item.Position);
            if (attachments.TryGetValue(key, out var old))
            {
                attachments[key] = item with
                {
                    MediaUrl = PreferSource(old.MediaUrl, item.MediaUrl),
                    PosterUrl = PreferSource(old.PosterUrl, item.PosterUrl),
                    AltText = string.IsNullOrWhiteSpace(item.AltText) ||
                        (item.AltText is "Image" or "画像" && !string.IsNullOrWhiteSpace(old.AltText))
                        ? old.AltText : item.AltText
                };
            }
            else
            {
                attachments.Add(key, item);
            }
        }
        return attachments.Values.OrderBy(item => item.Position).ThenBy(item => item.Kind, StringComparer.Ordinal).ToArray();
    }

    private static string? PreferSource(string? previous, string? current)
    {
        if (current is null)
        {
            return previous;
        }

        // The CDN may change only the requested display size when the window is resized.
        if (Uri.TryCreate(previous, UriKind.Absolute, out var old) &&
            Uri.TryCreate(current, UriKind.Absolute, out var newer) &&
            old.Host == "pbs.twimg.com" && old.GetLeftPart(UriPartial.Path) == newer.GetLeftPart(UriPartial.Path))
        {
            return previous;
        }

        return current;
    }

    public async Task<(int Saved, int Unavailable)> SaveAsync(IEnumerable<XPost> posts, CancellationToken cancellationToken)
    {
        var saved = 0;
        var unavailable = 0;
        await ArchiveWrite.WaitAsync(cancellationToken);
        try
        {
            foreach (var post in posts.Where(post => IsPostId(post.Id) && post.Attachments.Length > 0))
            {
                var previous = await ReadAsync(post.Id, cancellationToken);
                var current = Merge(previous.Select(item => item.Source), post.Attachments);
                var indexed = previous.ToDictionary(item => (item.Source.Kind, item.Source.Position));
                var entries = new List<ArchivedPostAttachment>();
                foreach (var item in current)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    indexed.TryGetValue((item.Kind, item.Position), out var old);
                    var local = await DownloadAsync(post.Id, item.MediaUrl, item.Kind == "video",
                        old?.Source.MediaUrl == item.MediaUrl ? old?.LocalPath : null, cancellationToken);
                    var poster = await DownloadAsync(post.Id, item.PosterUrl, false,
                        old?.Source.PosterUrl == item.PosterUrl ? old?.LocalPosterPath : null, cancellationToken);
                    entries.Add(new ArchivedPostAttachment(item, local, poster));
                    if (local is not null)
                    {
                        saved++;
                    }
                    else
                    {
                        unavailable++;
                    }
                }

                await WriteManifestAsync(post.Id, entries, cancellationToken);
            }
        }
        finally
        {
            ArchiveWrite.Release();
        }
        return (saved, unavailable);
    }

    public async Task<ArchivedPostAttachment[]> ReadAsync(string postId, CancellationToken cancellationToken)
    {
        if (!IsPostId(postId))
        {
            return [];
        }

        var path = ManifestPath(postId);
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<ArchivedPostAttachment[]>(stream, JsonOptions, cancellationToken) ?? [];
    }

    public string? ResolveFile(string postId, string? relativePath)
    {
        if (relativePath is null || !IsPostId(postId))
        {
            return null;
        }

        var prefix = $"media/{postId}/";
        if (!relativePath.StartsWith(prefix, StringComparison.Ordinal) ||
            !MediaFilePattern().IsMatch(relativePath[prefix.Length..]))
        {
            return null;
        }

        var path = Path.Combine(directory, relativePath);
        return File.Exists(path) ? path : null;
    }

    private async Task<string?> DownloadAsync(string postId, string? source, bool video, string? existing,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || !IsAllowedSource(uri, video))
        {
            return null;
        }
        if (ResolveFile(postId, existing) is not null)
        {
            return existing;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..32];
        var limit = video ? 128L * 1024 * 1024 : 32L * 1024 * 1024;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(video ? 45 : 15));
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var extension = FileExtension(response.Content.Headers.ContentType?.MediaType, video);
            if (!response.IsSuccessStatusCode || extension is null || response.Content.Headers.ContentLength > limit)
            {
                return null;
            }

            var relative = $"media/{postId}/{hash}{extension}";
            if (ResolveFile(postId, relative) is not null)
            {
                return relative;
            }

            var file = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                await using (var output = File.Create(temporary))
                {
                    var buffer = new byte[81920];
                    long length = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer.AsMemory(), timeout.Token)) != 0)
                    {
                        length += count;
                        if (length > limit)
                        {
                            return null;
                        }
                        await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    }
                    if (length == 0 || response.Content.Headers.ContentLength is long expected && expected != length)
                    {
                        return null;
                    }
                }
                File.Move(temporary, file, overwrite: true);
                return relative;
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or HttpIOException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static bool IsAllowedSource(Uri uri, bool video) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (video
            ? uri.Host == "video.twimg.com" && uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            : uri.Host == "pbs.twimg.com" && ImagePathPattern().IsMatch(uri.AbsolutePath));

    private static string? FileExtension(string? contentType, bool video) => (contentType, video) switch
    {
        ("video/mp4", true) => ".mp4",
        ("image/jpeg", false) => ".jpg",
        ("image/png", false) => ".png",
        ("image/webp", false) => ".webp",
        ("image/gif", false) => ".gif",
        ("image/avif", false) => ".avif",
        _ => null
    };

    private async Task WriteManifestAsync(string postId, List<ArchivedPostAttachment> entries,
        CancellationToken cancellationToken)
    {
        var path = ManifestPath(postId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, entries, JsonOptions, cancellationToken);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private string ManifestPath(string postId) => Path.Combine(directory, "posts", postId + ".json");

    private static bool IsPostId(string id) => id.Length is >= 1 and <= 20 && id.All(char.IsAsciiDigit);

    [GeneratedRegex(@"\A[0-9a-f]{32}\.(?:jpg|png|webp|gif|avif|mp4)\z")]
    private static partial Regex MediaFilePattern();

    [GeneratedRegex(@"\A/(?:media|ext_tw_video_thumb|amplify_video_thumb|tweet_video_thumb)/")]
    private static partial Regex ImagePathPattern();
}

internal sealed record ArchivedPostAttachment(PostAttachment Source, string? LocalPath, string? LocalPosterPath);
