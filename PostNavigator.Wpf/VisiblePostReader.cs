using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace PostNavigator.Wpf;

internal static class VisiblePostReader
{
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<string> Script = new(() => LoadScript("ReadVisiblePosts.js"));
    private static readonly Lazy<string> ObserveScript = new(() => LoadScript("ObserveVisiblePosts.js"));

    public static async Task<VisiblePostCapture> ReadAsync(CoreWebView2 browser, string? sessionId = null)
    {
        var script = sessionId is null ? Script.Value : ObserveScript.Value
            .Replace("__READ_VISIBLE_POSTS__", Script.Value, StringComparison.Ordinal)
            .Replace("__SESSION_ID__", JsonSerializer.Serialize(sessionId), StringComparison.Ordinal);
        var json = await browser.ExecuteScriptAsync(script);
        return JsonSerializer.Deserialize<VisiblePostCapture>(json, ReadOptions)
            ?? throw new InvalidOperationException("Xの画面から投稿データを読み取れませんでした");
    }

    public static async Task StopObservingAsync(CoreWebView2 browser, string sessionId)
    {
        try
        {
            await browser.ExecuteScriptAsync(
                $"window.__postNavigatorCapture?.stop({JsonSerializer.Serialize(sessionId)});");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // A navigation or closing the browser may already have discarded the observer.
        }
    }

    public static ObservedPostMessage? ReadMessage(string json)
    {
        return JsonSerializer.Deserialize<ObservedPostMessage>(json, ReadOptions);
    }

    private static string LoadScript(string fileName)
    {
        var resourceName = $"{typeof(VisiblePostReader).Namespace}.{fileName}";
        using var stream = typeof(VisiblePostReader).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("投稿取得用のスクリプトが見つかりません");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

internal sealed record VisiblePostCapture(int VisibleCards, int UnreadableCards, XPost[] Posts);
internal sealed record ObservedPostMessage(string Kind, string SessionId, VisiblePostCapture Capture);
