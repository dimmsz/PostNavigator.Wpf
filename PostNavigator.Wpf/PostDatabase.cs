using System.IO;
using MySqlConnector;

namespace PostNavigator.Wpf;

internal static class PostDatabase
{
    private static readonly Lazy<string> UpsertSql = new(LoadUpsertSql);

    public static async Task SaveAsync(string connectionString, IReadOnlyList<XPost> posts,
        CancellationToken cancellationToken)
    {
        if (posts.Count == 0)
        {
            return;
        }

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new MySqlCommand(UpsertSql.Value, connection, transaction);

        var id = command.Parameters.Add("@PostId", MySqlDbType.VarChar);
        var handle = command.Parameters.Add("@AuthorHandle", MySqlDbType.VarChar);
        var displayName = command.Parameters.Add("@AuthorDisplayName", MySqlDbType.VarChar);
        var text = command.Parameters.Add("@PostText", MySqlDbType.MediumText);
        var publishedAt = command.Parameters.Add("@PublishedAtUtc", MySqlDbType.DateTime);
        var url = command.Parameters.Add("@PostUrl", MySqlDbType.VarChar);
        var truncated = command.Parameters.Add("@IsTextTruncated", MySqlDbType.Byte);

        // Acquire row locks in the same order when multiple sessions save overlapping batches.
        foreach (var post in posts.OrderBy(post => post.Id, StringComparer.Ordinal))
        {
            id.Value = post.Id;
            handle.Value = post.AuthorHandle;
            displayName.Value = (object?)post.AuthorDisplayName ?? DBNull.Value;
            text.Value = post.Text;
            publishedAt.Value = post.PublishedAtUtc.UtcDateTime;
            url.Value = post.Url;
            truncated.Value = post.IsTextTruncated ? 1 : 0;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static string LoadUpsertSql()
    {
        using var stream = typeof(PostDatabase).Assembly.GetManifestResourceStream("PostNavigator.Wpf.UpsertXPost.sql")
            ?? throw new InvalidOperationException("投稿保存用のSQLが見つかりません");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
