using MySqlConnector;

namespace PostNavigator.Wpf;

internal static class DatabaseConnectionTest
{
    public static bool HasValidOptions(string connectionString)
    {
        try
        {
            return new MySqlConnectionStringBuilder(connectionString).Count > 0;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            return false;
        }
    }

    public static async Task<string?> GetErrorAsync(string connectionString, CancellationToken cancellationToken)
    {
        var options = new MySqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            ConnectionTimeout = 10
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var connection = new MySqlConnection(options.ConnectionString);

        try
        {
            await connection.OpenAsync(timeout.Token);
            await using var command = new MySqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(timeout.Token);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "接続テストがタイムアウトしました。接続先とネットワークを確認してください。";
        }
        catch (MySqlException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return "接続テストがタイムアウトしました。接続先とネットワークを確認してください。";
        }
        catch (MySqlException ex)
        {
            return ex.Number switch
            {
                1044 => "指定したデータベースへのアクセス権がありません（1044）。",
                1045 => "認証に失敗しました。ユーザー名とパスワードを確認してください（1045）。",
                1049 => "指定したデータベースが見つかりません（1049）。",
                _ => $"接続できませんでした。サーバー名、ポート、SSL設定を確認してください（コード {ex.Number}）。"
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return "接続を開始できませんでした。接続文字列とSSL設定を確認してください。";
        }
    }
}
