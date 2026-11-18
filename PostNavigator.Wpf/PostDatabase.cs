using System.IO;
using MySqlConnector;

namespace PostNavigator.Wpf;

internal static class PostDatabase
{
    private static readonly Lazy<string> UpsertSql = new(() => LoadSql("UpsertXPost.sql"));
    private static readonly Lazy<string> SelectSql = new(() => LoadSql("SelectXPosts.sql"));

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

    public static async Task<List<XPost>> ReadAsync(string connectionString, string account, DateOnly start,
        DateOnly end, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(SelectSql.Value, connection);
        command.Parameters.AddWithValue("@AuthorHandle", account);
        command.Parameters.AddWithValue("@StartUtc", start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        command.Parameters.AddWithValue("@EndUtc", end.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        var posts = new List<XPost>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            posts.Add(new XPost(reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)),
                reader.GetString(5), reader.GetBoolean(6)));
        }
        return posts;
    }

    private static string LoadSql(string name)
    {
        using var stream = typeof(PostDatabase).Assembly.GetManifestResourceStream($"PostNavigator.Wpf.{name}")
            ?? throw new InvalidOperationException($"投稿用SQLが見つかりません。{name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
