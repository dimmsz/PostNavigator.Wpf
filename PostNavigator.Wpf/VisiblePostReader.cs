using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace PostNavigator.Wpf;

internal static class VisiblePostReader
{
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<string> Script = new(LoadScript);

    public static async Task<XPost[]> ReadAsync(CoreWebView2 browser)
    {
        var json = await browser.ExecuteScriptAsync(Script.Value);
        return JsonSerializer.Deserialize<XPost[]>(json, ReadOptions)
            ?? throw new InvalidOperationException("Xの画面から投稿データを読み取れませんでした");
    }

    private static string LoadScript()
    {
        var resourceName = $"{typeof(VisiblePostReader).Namespace}.ReadVisiblePosts.js";
        using var stream = typeof(VisiblePostReader).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("投稿取得用のスクリプトが見つかりません");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
