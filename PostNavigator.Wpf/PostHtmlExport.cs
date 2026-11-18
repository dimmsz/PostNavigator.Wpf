using System.Globalization;
using System.IO;
using System.Net;
using System.Text.Json;

namespace PostNavigator.Wpf;

internal static class PostHtmlExport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<string> WriteAsync(string folder, string account, DateOnly start, DateOnly end,
        IReadOnlyList<XPost> posts, PostMediaArchive archive, CancellationToken cancellationToken)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = FormattableString.Invariant($"x-{account}-{start:yyyyMMdd}-{suffix}");
        var staging = Path.Combine(folder, "." + name);
        var destination = Path.Combine(folder, name);
        Directory.CreateDirectory(staging);
        try
        {
            var mediaIndex = new Dictionary<string, List<ArchivedPostAttachment>>(StringComparer.Ordinal);
            await using (var html = new StreamWriter(Path.Combine(staging, "index.html")))
            {
                await html.WriteLineAsync("""
                    <!doctype html>
                    <html lang="ja"><meta charset="utf-8">
                    <meta name="viewport" content="width=device-width,initial-scale=1">
                    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src 'self' file:; media-src 'self' file:; style-src 'unsafe-inline'">
                    <title>保存したXの投稿</title>
                    <style>
                    :root{color-scheme:light dark;font-family:system-ui,sans-serif}
                    body{max-width:48rem;margin:2rem auto;padding:0 1rem;color:CanvasText;background:Canvas}
                    h1{font-size:1.6rem;margin-bottom:.4rem}a{color:LinkText}
                    article{border:1px solid GrayText;border-radius:.8rem;margin:1.2rem 0;padding:1rem}
                    .body{white-space:pre-wrap;overflow-wrap:anywhere;line-height:1.6}
                    .details{font-size:.85rem}figure{margin:1rem 0}img,video{display:block;max-width:100%;max-height:46rem;border-radius:.5rem}
                    figcaption{margin-top:.4rem;font-size:.85rem}
                    </style><main>
                    """);
                await html.WriteLineAsync($"<h1>@{Encode(account)} の保存済み投稿</h1>");
                await html.WriteLineAsync(FormattableString.Invariant(
                    $"<p>{start:yyyy-MM-dd} ～ {end.AddDays(-1):yyyy-MM-dd}（UTC） · {posts.Count}件</p>"));

                foreach (var post in posts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var author = string.IsNullOrWhiteSpace(post.AuthorDisplayName) ? post.AuthorHandle : post.AuthorDisplayName;
                    var time = post.PublishedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
                    await html.WriteLineAsync($"<article><strong>{Encode(author)}</strong> @{Encode(post.AuthorHandle)}");
                    await html.WriteLineAsync($"<p class=\"details\">{Encode(time)} {XLink(post.Url, "Xで元投稿を開く")}</p>");
                    await html.WriteLineAsync($"<p class=\"body\">{Encode(post.Text)}</p>");
                    if (post.IsTextTruncated)
                    {
                        await html.WriteLineAsync("<p class=\"details\">保存時に本文の一部が省略されていました。</p>");
                    }

                    var attachments = new List<ArchivedPostAttachment>();
                    foreach (var item in await archive.ReadAsync(post.Id, cancellationToken))
                    {
                        var local = await CopyMediaAsync(archive, staging, post.Id, item.LocalPath, cancellationToken);
                        var poster = await CopyMediaAsync(archive, staging, post.Id, item.LocalPosterPath, cancellationToken);
                        var exported = item with { LocalPath = local, LocalPosterPath = poster };
                        attachments.Add(exported);
                        await WriteAttachmentAsync(html, exported);
                    }
                    if (attachments.Count > 0)
                    {
                        mediaIndex.Add(post.Id, attachments);
                    }
                    await html.WriteLineAsync("</article>");
                }
                await html.WriteLineAsync("</main></html>");
            }

            await using (var index = File.Create(Path.Combine(staging, "attachments.json")))
            {
                await JsonSerializer.SerializeAsync(index, mediaIndex, JsonOptions, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return Path.Combine(destination, "index.html");
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static async Task<string?> CopyMediaAsync(PostMediaArchive archive, string destination, string postId,
        string? relative, CancellationToken cancellationToken)
    {
        var source = archive.ResolveFile(postId, relative);
        if (source is null || relative is null)
        {
            return null;
        }

        var target = Path.Combine(destination, relative);
        if (!File.Exists(target))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = File.OpenRead(source);
            await using var output = File.Create(target);
            await input.CopyToAsync(output, cancellationToken);
        }
        return relative;
    }

    private static async Task WriteAttachmentAsync(StreamWriter html, ArchivedPostAttachment item)
    {
        var isVideo = item.Source.Kind == "video";
        var label = isVideo ? "動画" : "画像";
        await html.WriteLineAsync("<figure>");
        if (isVideo && item.LocalPath is { } video)
        {
            var poster = item.LocalPosterPath is { } preview ? $" poster=\"{Encode(preview)}\"" : "";
            await html.WriteLineAsync($"<video controls preload=\"metadata\" src=\"{Encode(video)}\"{poster}></video>");
        }
        else if ((isVideo ? item.LocalPosterPath : item.LocalPath) is { } image)
        {
            var alt = Encode(item.Source.AltText ?? (isVideo ? "動画のサムネイル" : "添付画像"));
            await html.WriteLineAsync($"<a href=\"{Encode(image)}\"><img loading=\"lazy\" src=\"{Encode(image)}\" alt=\"{alt}\"></a>");
        }
        var state = item.LocalPath is null ? $"{label}本体未保存。" : "";
        await html.WriteLineAsync($"<figcaption>{state}{XLink(item.Source.LinkUrl, $"Xで{label}を見る")}</figcaption></figure>");
    }

    private static string XLink(string url, string label) =>
        XNavigation.IsXPage(url) ? $"<a href=\"{Encode(url)}\" rel=\"noreferrer\">{Encode(label)}</a>" : "";

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
