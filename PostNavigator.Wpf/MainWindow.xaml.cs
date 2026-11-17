using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using MySqlConnector;

namespace PostNavigator.Wpf;

public partial class MainWindow : Window
{
    private const string InitialAccount = "cissan_9984";
    private readonly DispatcherTimer automaticCaptureTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(750)
    };
    private ulong activeNavigation;
    private bool navigationInProgress;
    private bool closed;
    private bool settingsOpen;
    private bool captureOperationIsAutomatic;
    private CancellationTokenSource? captureOperation;
    private AutomaticCaptureSession? automaticCaptureSession;

    public MainWindow()
    {
        InitializeComponent();
        AccountBox.Text = $"@{InitialAccount}";

        var currentYear = DateTime.Today.Year;
        YearBox.ItemsSource = Enumerable.Range(XNavigation.FirstYear, currentYear - XNavigation.FirstYear + 1)
            .Reverse();
        YearBox.SelectedItem = currentYear;

        MonthBox.ItemsSource = new[] { new MonthOption(0, "通年") }
            .Concat(Enumerable.Range(1, 12).Select(month => new MonthOption(month, $"{month}月")));
        MonthBox.SelectedValue = 0;

        automaticCaptureTimer.Tick += CaptureAutomatically;
        Loaded += InitializeBrowser;
    }

    private async void InitializeBrowser(object sender, RoutedEventArgs e)
    {
        Loaded -= InitializeBrowser;

        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PostNavigator", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            if (closed)
            {
                return;
            }

            await Browser.EnsureCoreWebView2Async(environment);
            if (closed)
            {
                return;
            }

            var web = Browser.CoreWebView2;
            web.NavigationStarting += NavigationStarting;
            web.NavigationCompleted += NavigationCompleted;
            web.HistoryChanged += (_, _) => UpdateHistory();
            web.SourceChanged += (_, _) => UpdatePageLabel();
            web.DocumentTitleChanged += (_, _) => UpdatePageLabel();

            BrowserNotice.Visibility = Visibility.Collapsed;
            Browser.Visibility = Visibility.Visible;
            SearchPanel.IsEnabled = true;
            HomeButton.IsEnabled = true;
            ReloadButton.IsEnabled = true;
            SignInButton.IsEnabled = true;
            web.Navigate(XNavigation.Profile(InitialAccount, includeReplies: false).AbsoluteUri);
        }
        catch (WebView2RuntimeNotFoundException) when (!closed)
        {
            ShowStartupError("WebView2 Runtimeが見つかりません",
                "Evergreen Runtimeをインストールして、アプリを起動し直してください。");
            RuntimeButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            if (!closed)
            {
                ShowStartupError("ブラウザを起動できませんでした", ex.Message);
            }
        }
    }

    private void ShowStartupError(string title, string detail)
    {
        Browser.Visibility = Visibility.Hidden;
        BrowserNotice.Visibility = Visibility.Visible;
        BrowserNoticeTitle.Text = title;
        BrowserNoticeDetail.Text = detail;
        BrowserNoticeDetail.Visibility = Visibility.Visible;
        Status.Text = title;
    }

    private void OpenRuntimeDownload(object sender, RoutedEventArgs e)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(
                "https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Status.Text = "ダウンロードページを開けませんでした。";
        }
    }

    private bool TryReadAccount(out string account)
    {
        if (XNavigation.TryGetAccount(AccountBox.Text, out account))
        {
            return true;
        }

        AccountError.Text = "ユーザー名（英数字と _ の1〜15文字）またはXのプロフィールURLを入力してください。";
        AccountError.Visibility = Visibility.Visible;
        AccountBox.Focus();
        return false;
    }

    private void AccountChanged(object sender, RoutedEventArgs e)
    {
        if (AccountError is not null)
        {
            AccountError.Visibility = Visibility.Collapsed;
        }
    }

    private void SearchOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SearchPosts(sender, e);
        }
    }

    private void SearchPosts(object sender, RoutedEventArgs e)
    {
        if (TryReadAccount(out var account) && YearBox.SelectedItem is int year && MonthBox.SelectedValue is int month)
        {
            var (start, end) = XNavigation.DateRange(year, month);
            Navigate(XNavigation.Search(account, start, end));
        }
    }

    private async void CaptureVisiblePosts(object sender, RoutedEventArgs e)
    {
        await CaptureAndSaveVisiblePostsAsync(null);
    }

    private void AutomaticCaptureChanged(object sender, RoutedEventArgs e)
    {
        if (AutomaticCaptureSwitch.IsChecked == true)
        {
            StartAutomaticCapture();
        }
        else if (automaticCaptureSession is not null)
        {
            StopAutomaticCapture();
            Status.Text = "自動取得を停止しました。";
        }
    }

    private void StartAutomaticCapture()
    {
        if (!TryLoadCaptureConnection(out var connectionString))
        {
            StopAutomaticCapture();
            return;
        }

        automaticCaptureSession = new AutomaticCaptureSession(connectionString);
        automaticCaptureTimer.Start();
        Status.Text = "自動取得を開始しました。画面内の投稿をデータベースに保存します。";
    }

    private void StopAutomaticCapture()
    {
        automaticCaptureTimer.Stop();
        automaticCaptureSession = null;
        AutomaticCaptureSwitch.IsChecked = false;
        if (captureOperationIsAutomatic)
        {
            captureOperation?.Cancel();
        }
    }

    private async void CaptureAutomatically(object? sender, EventArgs e)
    {
        if (automaticCaptureSession is { } session && !navigationInProgress && !settingsOpen &&
            WindowState != WindowState.Minimized)
        {
            await CaptureAndSaveVisiblePostsAsync(session);
        }
    }

    private bool TryLoadCaptureConnection(out string connectionString)
    {
        connectionString = string.Empty;
        try
        {
            connectionString = DatabaseSettings.LoadConnectionString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Status.Text = "接続文字列を読み込めませんでした。設定画面で保存し直してください。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(connectionString) || !DatabaseConnectionTest.HasValidOptions(connectionString))
        {
            Status.Text = "設定画面でデータベースの接続文字列を登録してください。";
            return false;
        }

        return true;
    }

    private async Task CaptureAndSaveVisiblePostsAsync(AutomaticCaptureSession? automaticSession)
    {
        if (closed || captureOperation is not null)
        {
            return;
        }

        var isAutomatic = automaticSession is not null;
        if (Browser.CoreWebView2 is not { } web || !XNavigation.IsXPage(web.Source))
        {
            if (!isAutomatic)
            {
                Status.Text = "Xで投稿を表示してから取得してください。";
            }
            return;
        }

        var connectionString = automaticSession?.ConnectionString ?? string.Empty;
        if (!isAutomatic && !TryLoadCaptureConnection(out connectionString))
        {
            return;
        }

        var candidateSession = automaticSession ?? automaticCaptureSession;
        var navigationId = activeNavigation;
        var source = web.Source;
        var readingViewport = true;
        using var operation = new CancellationTokenSource();
        captureOperation = operation;
        captureOperationIsAutomatic = isAutomatic;
        if (!isAutomatic)
        {
            CapturePostsButton.IsEnabled = false;
            Status.Text = "表示中の投稿を取得しています…";
        }

        try
        {
            var capture = await VisiblePostReader.ReadAsync(web);
            if (closed || operation.IsCancellationRequested || (isAutomatic && AutomaticViewportChanged()))
            {
                return;
            }

            readingViewport = false;
            var posts = capture.Posts;
            if (posts.Length == 0)
            {
                if (!isAutomatic || capture.VisibleCards > 0)
                {
                    Status.Text = capture.VisibleCards == 0
                        ? "画面内の投稿カードを認識できませんでした。投稿一覧を表示して再試行してください。"
                        : $"投稿カードを{capture.VisibleCards}件検出しましたが、投稿ID・投稿者・日時のいずれかを読み取れませんでした。";
                }
                return;
            }

            if (automaticSession is not null)
            {
                posts = automaticSession.PendingPosts(posts);
                if (posts.Length == 0)
                {
                    return;
                }
            }

            var skipped = capture.UnreadableCards > 0 ? $" 未取得 {capture.UnreadableCards}件。" : "";
            CapturePostsButton.IsEnabled = false;
            Status.Text = $"{posts.Length}件の投稿をデータベースに保存しています…";
            await PostDatabase.SaveAsync(connectionString, posts, operation.Token);
            if (candidateSession is not null && ReferenceEquals(candidateSession, automaticCaptureSession) &&
                string.Equals(connectionString, candidateSession.ConnectionString, StringComparison.Ordinal))
            {
                candidateSession.MarkSaved(posts);
            }
            if (!closed && !operation.IsCancellationRequested)
            {
                var prefix = isAutomatic ? "自動取得で" : "";
                Status.Text = $"{prefix}データベースに{posts.Length}件の投稿を保存しました。{skipped}";
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            // The window closed or automatic capture was switched off.
        }
        catch (MySqlException ex)
        {
            if (!closed && !operation.IsCancellationRequested)
            {
                ReportCaptureError(ex.Number switch
                {
                    1044 or 1142 or 1143 => "DBへの保存に必要な権限がありません。接続ユーザーの権限を確認してください。",
                    1045 => "DBの認証に失敗しました。設定画面で接続テストを実行してください。",
                    1046 or 1049 => "保存先のデータベース名を設定画面で確認してください。",
                    1146 => "保存先に x_posts テーブルが見つかりません。接続先とテーブルを確認してください。",
                    1205 or 1213 => "DBで同時更新が発生しました。もう一度、表示中の投稿を取得してください。",
                    _ => $"DBへの保存に失敗しました（コード {ex.Number}）。"
                }, isAutomatic);
            }
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException
            or InvalidOperationException or JsonException or TimeoutException or ArgumentException)
        {
            if (!closed && !operation.IsCancellationRequested &&
                (!isAutomatic || !readingViewport || !AutomaticViewportChanged()))
            {
                ReportCaptureError("投稿を取得または保存できませんでした。検索結果とDBの接続設定を確認してください。",
                    isAutomatic);
            }
        }
        finally
        {
            captureOperation = null;
            captureOperationIsAutomatic = false;
            if (!closed)
            {
                CapturePostsButton.IsEnabled = true;
            }
        }

        bool AutomaticViewportChanged() => settingsOpen || WindowState == WindowState.Minimized ||
            navigationInProgress || navigationId != activeNavigation ||
            !string.Equals(web.Source, source, StringComparison.Ordinal);
    }

    private void ReportCaptureError(string message, bool isAutomatic)
    {
        if (isAutomatic)
        {
            StopAutomaticCapture();
            message = $"自動取得を停止しました。{message}";
        }
        Status.Text = message;
    }

    private void OpenProfile(object sender, RoutedEventArgs e) => OpenAccount(includeReplies: false);
    private void OpenReplies(object sender, RoutedEventArgs e) => OpenAccount(includeReplies: true);

    private void OpenAccount(bool includeReplies)
    {
        if (TryReadAccount(out var account))
        {
            Navigate(XNavigation.Profile(account, includeReplies));
        }
    }

    private void OpenSignIn(object sender, RoutedEventArgs e) => Navigate(XNavigation.SignIn);
    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        settingsOpen = true;
        try
        {
            var settings = new SettingsWindow { Owner = this };
            if (settings.ShowDialog() == true)
            {
                Status.Text = "データベース接続設定を保存しました。";
                if (automaticCaptureSession is not null)
                {
                    StopAutomaticCapture();
                    AutomaticCaptureSwitch.IsChecked = true;
                }
            }
        }
        finally
        {
            settingsOpen = false;
        }
    }

    private void OpenHome(object sender, RoutedEventArgs e) => Navigate(XNavigation.Home);
    private void Navigate(Uri uri) => Browser.CoreWebView2?.Navigate(uri.AbsoluteUri);
    private void GoBack(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.GoBack();
    private void GoForward(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.GoForward();
    private void ReloadPage(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Reload();

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        activeNavigation = e.NavigationId;
        navigationInProgress = true;
        if (captureOperation is null)
        {
            Status.Text = "読み込み中…";
        }
    }

    private void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        UpdateHistory();
        UpdatePageLabel();
        if (e.NavigationId != activeNavigation)
        {
            return;
        }

        navigationInProgress = false;
        if (captureOperation is not null)
        {
            return;
        }

        if (e.IsSuccess)
        {
            Status.Text = "読み込み完了";
        }
        else if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
        {
            Status.Text = "読み込みを中断しました。";
        }
        else
        {
            var error = e.HttpStatusCode >= 400 ? $"HTTP {e.HttpStatusCode}" : e.WebErrorStatus.ToString();
            Status.Text = $"ページを読み込めませんでした（{error}）。";
        }
    }

    private void UpdateHistory()
    {
        var web = Browser.CoreWebView2;
        BackButton.IsEnabled = web.CanGoBack;
        ForwardButton.IsEnabled = web.CanGoForward;
    }

    private void UpdatePageLabel()
    {
        var web = Browser.CoreWebView2;
        var title = web.DocumentTitle;
        PageLabel.Text = string.IsNullOrWhiteSpace(title) ? web.Source : title;
        PageLabel.ToolTip = web.Source;
    }

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        automaticCaptureTimer.Stop();
        captureOperation?.Cancel();
        Browser.Dispose();
        base.OnClosed(e);
    }

    private sealed record MonthOption(int Number, string Label);
}
