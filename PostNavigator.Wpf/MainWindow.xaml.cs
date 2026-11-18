using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using MySqlConnector;

namespace PostNavigator.Wpf;

public partial class MainWindow : Window
{
    private const string InitialAccount = "cissan_9984";
    private readonly DispatcherTimer automaticCaptureTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(750)
    };
    private readonly DispatcherTimer slowScrollTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(50)
    };
    private readonly PostMediaArchive mediaArchive = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PostNavigator", "PostArchive"));
    private ulong activeNavigation;
    private bool navigationInProgress;
    private bool closed;
    private bool settingsOpen;
    private int slowScrollDirection;
    private int slowScrollRun;
    private bool scrollStepInProgress;
    private long? scrollBlockedSince;
    private bool captureOperationIsAutomatic;
    private CancellationTokenSource? captureOperation;
    private CancellationTokenSource? exportOperation;
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
        slowScrollTimer.Tick += ScrollStep;
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
            web.SourceChanged += (_, _) =>
            {
                StopSlowScroll();
                UpdatePageLabel();
            };
            web.DocumentTitleChanged += (_, _) => UpdatePageLabel();
            web.WebMessageReceived += PostsObserved;

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

    private void ScrollSlowlyUp(object sender, RoutedEventArgs e) => StartSlowScroll(-1);
    private void ScrollSlowlyDown(object sender, RoutedEventArgs e) => StartSlowScroll(1);
    private void StopSlowScrolling(object sender, RoutedEventArgs e) => StopSlowScroll("停止しました。");

    private void StartSlowScroll(int direction)
    {
        if (navigationInProgress || Browser.CoreWebView2 is not { } web || !XNavigation.IsXPage(web.Source))
        {
            return;
        }

        slowScrollRun++;
        slowScrollDirection = direction;
        scrollBlockedSince = null;
        ScrollStatus.Text = direction > 0 ? "下へスクロール中…" : "上へスクロール中…";
        UpdateSlowScrollButtons();
        slowScrollTimer.Start();
    }

    private void StopSlowScroll(string message = "")
    {
        if (slowScrollDirection != 0)
        {
            slowScrollTimer.Stop();
            slowScrollRun++;
            slowScrollDirection = 0;
        }

        ScrollStatus.Text = message;
        UpdateSlowScrollButtons();
    }

    private void UpdateSlowScrollButtons()
    {
        var available = !navigationInProgress && Browser.CoreWebView2 is { } web && XNavigation.IsXPage(web.Source);
        ScrollUpButton.IsEnabled = available && slowScrollDirection != -1;
        ScrollDownButton.IsEnabled = available && slowScrollDirection != 1;
        StopScrollButton.IsEnabled = slowScrollDirection != 0;
    }

    private async void ScrollStep(object? sender, EventArgs e)
    {
        if (closed || scrollStepInProgress || slowScrollDirection == 0 || Browser.CoreWebView2 is not { } web)
        {
            return;
        }

        var run = slowScrollRun;
        var direction = slowScrollDirection;
        var step = direction > 0 ? "6" : "-6";
        var source = JsonSerializer.Serialize(web.Source);
        scrollStepInProgress = true;
        try
        {
            var moved = await web.ExecuteScriptAsync($$"""
                (() => {
                    if (location.href !== new URL({{source}}).href) return null;

                    let container = document.querySelector('[data-testid="primaryColumn"]');
                    while (container && container !== document.body && container !== document.documentElement) {
                        if (container.scrollHeight > container.clientHeight) {
                            const overflow = getComputedStyle(container).overflowY;
                            if (overflow === "auto" || overflow === "scroll") break;
                        }
                        container = container.parentElement;
                    }

                    if (!container || container === document.body || container === document.documentElement) {
                        container = document.scrollingElement;
                    }
                    if (!container) return false;

                    const previousTop = container.scrollTop;
                    container.scrollBy({ top: {{step}}, behavior: "instant" });
                    return container.scrollTop !== previousTop;
                })()
                """);

            if (closed || run != slowScrollRun)
            {
                return;
            }

            switch (moved)
            {
                case "true":
                    scrollBlockedSince = null;
                    break;
                case "false":
                    // X can append more posts after the viewport first reaches the bottom.
                    scrollBlockedSince ??= Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(scrollBlockedSince.Value) >= TimeSpan.FromSeconds(3))
                    {
                        StopSlowScroll(direction > 0 ? "読み込み済みの投稿の下端に達しました。" : "上端に達しました。");
                    }
                    break;
                default:
                    StopSlowScroll("自動スクロールを中断しました。");
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            if (!closed && run == slowScrollRun)
            {
                StopSlowScroll("自動スクロールを中断しました。");
            }
        }
        finally
        {
            scrollStepInProgress = false;
        }
    }

    private async void ExportHtml(object sender, RoutedEventArgs e)
    {
        if (exportOperation is not null || !TryReadAccount(out var account) ||
            YearBox.SelectedItem is not int year || MonthBox.SelectedValue is not int month ||
            !TryLoadCaptureConnection(out var connectionString))
        {
            return;
        }

        StopSlowScroll();
        var dialog = new OpenFolderDialog { Title = "HTMLと添付ファイルの保存先を選ぶ" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        using var operation = new CancellationTokenSource();
        exportOperation = operation;
        ExportHtmlButton.IsEnabled = false;
        Status.Text = "保存済みの投稿をHTMLに書き出しています…";
        try
        {
            var (start, end) = XNavigation.DateRange(year, month);
            var posts = await PostDatabase.ReadAsync(connectionString, account, start, end, operation.Token);
            if (posts.Count == 0)
            {
                Status.Text = "指定した期間に保存済みの投稿はありません。";
                return;
            }

            var index = await PostHtmlExport.WriteAsync(dialog.FolderName, account, start, end, posts,
                mediaArchive, operation.Token);
            if (!closed)
            {
                Status.Text = $"{posts.Count}件を書き出しました。{index}";
                try
                {
                    using var process = Process.Start(new ProcessStartInfo(index) { UseShellExecute = true });
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // The exported file remains available when no default browser is set.
                }
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            // The window closed during export.
        }
        catch (Exception ex) when (ex is MySqlException or IOException or UnauthorizedAccessException or
            InvalidOperationException or JsonException or ArgumentException)
        {
            if (!closed)
            {
                Status.Text = "HTMLを書き出せませんでした。DBの接続と保存先を確認してください。";
            }
        }
        finally
        {
            exportOperation = null;
            if (!closed)
            {
                ExportHtmlButton.IsEnabled = true;
            }
        }
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
        UpdateCaptureProgress(automaticCaptureSession);
        automaticCaptureTimer.Start();
        Status.Text = "自動取得を開始しました。画面内の投稿をデータベースに保存します。";
    }

    private void StopAutomaticCapture()
    {
        automaticCaptureTimer.Stop();
        var session = automaticCaptureSession;
        automaticCaptureSession = null;
        AutomaticCaptureSwitch.IsChecked = false;
        if (captureOperationIsAutomatic)
        {
            captureOperation?.Cancel();
        }
        if (session is not null && Browser.CoreWebView2 is { } web)
        {
            _ = VisiblePostReader.StopObservingAsync(web, session.Id);
        }
    }

    private void PostsObserved(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (automaticCaptureSession is not { } session || closed || settingsOpen ||
            navigationInProgress || WindowState == WindowState.Minimized || !XNavigation.IsXPage(e.Source))
        {
            return;
        }

        try
        {
            var message = VisiblePostReader.ReadMessage(e.WebMessageAsJson);
            if (message is { Kind: "postNavigatorCapture", Capture.Posts: not null } &&
                string.Equals(message.SessionId, session.Id, StringComparison.Ordinal))
            {
                session.Observe(message.Capture.Posts);
                UpdateCaptureProgress(session);
            }
        }
        catch (JsonException)
        {
            // Other scripts on the page can also send WebView messages.
        }
    }

    private void UpdateCaptureProgress(AutomaticCaptureSession session)
    {
        var pending = session.PendingPosts(Array.Empty<XPost>()).Length;
        CaptureProgress.Text = $"検出 {session.ObservedCount}件 / 保存確認 {session.SavedCount}件 / 保存待ち {pending}件";
        CaptureProgress.Visibility = Visibility.Visible;
    }

    private async void CaptureAutomatically(object? sender, EventArgs e)
    {
        if (automaticCaptureSession is { } session && !navigationInProgress && !settingsOpen &&
            WindowState != WindowState.Minimized && DateTime.UtcNow >= session.RetryAfterUtc)
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
            var capture = await VisiblePostReader.ReadAsync(web, automaticSession?.Id);
            if (closed || operation.IsCancellationRequested || (isAutomatic && AutomaticViewportChanged()))
            {
                return;
            }

            readingViewport = false;
            var posts = automaticSession?.PendingPosts(capture.Posts) ?? capture.Posts;
            if (automaticSession is not null)
            {
                UpdateCaptureProgress(automaticSession);
            }
            if (posts.Length == 0)
            {
                if (!isAutomatic || capture.Posts.Length == 0 && capture.VisibleCards > 0)
                {
                    Status.Text = capture.VisibleCards == 0
                        ? "画面内の投稿カードを認識できませんでした。投稿一覧を表示して再試行してください。"
                        : $"投稿カードを{capture.VisibleCards}件検出しましたが、投稿ID・投稿者・日時のいずれかを読み取れませんでした。";
                }
                return;
            }

            var skipped = capture.UnreadableCards > 0 ? $" 未取得 {capture.UnreadableCards}件。" : "";
            CapturePostsButton.IsEnabled = false;
            Status.Text = $"{posts.Length}件の投稿をデータベースに保存しています…";
            await PostDatabase.SaveAsync(connectionString, posts, operation.Token);
            if (posts.Any(post => post.Attachments.Length > 0))
            {
                Status.Text = "投稿の添付ファイルを保存しています…";
            }
            var media = await mediaArchive.SaveAsync(posts, operation.Token);
            if (candidateSession is not null && ReferenceEquals(candidateSession, automaticCaptureSession) &&
                string.Equals(connectionString, candidateSession.ConnectionString, StringComparison.Ordinal))
            {
                candidateSession.MarkSaved(posts);
                UpdateCaptureProgress(candidateSession);
            }
            if (!closed && !operation.IsCancellationRequested)
            {
                var prefix = isAutomatic ? "自動取得で" : "";
                var attachments = media.Saved + media.Unavailable > 0
                    ? $" 添付本体 {media.Saved}件保存／未保存 {media.Unavailable}件。" : "";
                Status.Text = $"{prefix}データベースに{posts.Length}件の投稿を保存しました。{attachments}{skipped}";
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
                if (automaticSession is not null && ex.Number is 0 or 1042 or 1205 or 1213 or 2006 or 2013)
                {
                    automaticSession.RetryAfterUtc = DateTime.UtcNow.AddSeconds(3);
                    Status.Text = "DBへの保存を再試行します。上下にスクロールして戻った投稿も再確認します。";
                    return;
                }

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
        StopSlowScroll();
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
        StopSlowScroll();
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
        UpdateSlowScrollButtons();
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
        slowScrollTimer.Stop();
        captureOperation?.Cancel();
        exportOperation?.Cancel();
        Browser.Dispose();
        base.OnClosed(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && slowScrollDirection != 0)
        {
            StopSlowScroll();
        }
    }

    private sealed record MonthOption(int Number, string Label);
}
