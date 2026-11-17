using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PostNavigator.Wpf;

internal static class PostJsonFile
{
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    public static async Task SaveAsync(string path, XPost[] posts)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temporaryPath = Path.Combine(directory, $".post-navigator-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, bufferSize: 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, posts, WriteOptions);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
