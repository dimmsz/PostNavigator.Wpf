using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace PostNavigator.Wpf;

public partial class MainWindow : Window
{
    private const string InitialAccount = "cissan_9984";
    private ulong activeNavigation;
    private bool closed;

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
    private void OpenHome(object sender, RoutedEventArgs e) => Navigate(XNavigation.Home);
    private void Navigate(Uri uri) => Browser.CoreWebView2?.Navigate(uri.AbsoluteUri);
    private void GoBack(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.GoBack();
    private void GoForward(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.GoForward();
    private void ReloadPage(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Reload();

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        activeNavigation = e.NavigationId;
        Status.Text = "読み込み中…";
    }

    private void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        UpdateHistory();
        UpdatePageLabel();
        if (e.NavigationId != activeNavigation)
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
        Browser.Dispose();
        base.OnClosed(e);
    }

    private sealed record MonthOption(int Number, string Label);
}
