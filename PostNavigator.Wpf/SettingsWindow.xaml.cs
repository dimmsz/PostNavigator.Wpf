using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace PostNavigator.Wpf;

public partial class SettingsWindow : Window
{
    private CancellationTokenSource? connectionTest;

    public SettingsWindow()
    {
        InitializeComponent();
        try
        {
            MaskedConnectionString.Password = DatabaseSettings.LoadConnectionString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            ShowFeedback("保存済みの接続文字列を読み込めませんでした。接続文字列を入力して保存し直せます。",
                "SystemFillColorCriticalBrush");
        }
    }

    private string CurrentConnectionString => RevealConnectionString.IsChecked == true
        ? VisibleConnectionString.Text
        : MaskedConnectionString.Password;

    private void ToggleVisibility(object sender, RoutedEventArgs e)
    {
        if (RevealConnectionString.IsChecked == true)
        {
            VisibleConnectionString.Text = MaskedConnectionString.Password;
            MaskedConnectionString.Clear();
            MaskedConnectionString.Visibility = Visibility.Collapsed;
            VisibleConnectionString.Visibility = Visibility.Visible;
        }
        else
        {
            MaskedConnectionString.Password = VisibleConnectionString.Text;
            VisibleConnectionString.Clear();
            VisibleConnectionString.Visibility = Visibility.Collapsed;
            MaskedConnectionString.Visibility = Visibility.Visible;
        }
    }

    private void ConnectionChanged(object sender, RoutedEventArgs e)
    {
        if (ConnectionFeedback is not null)
        {
            ConnectionFeedback.Visibility = Visibility.Collapsed;
        }
    }

    private async void TestConnection(object sender, RoutedEventArgs e)
    {
        var value = CurrentConnectionString;
        if (string.IsNullOrWhiteSpace(value))
        {
            ShowFeedback("接続文字列を入力してください。", "SystemFillColorCriticalBrush");
            return;
        }
        if (!ValidateOptions(value))
        {
            return;
        }

        using var test = new CancellationTokenSource();
        connectionTest = test;
        SetTestInProgress(true);
        ShowFeedback("接続を確認しています…", "TextFillColorSecondaryBrush");

        try
        {
            var error = await DatabaseConnectionTest.GetErrorAsync(value, test.Token);
            if (!test.IsCancellationRequested)
            {
                ShowFeedback(error ?? "接続できました。",
                    error is null ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
            }
        }
        catch (OperationCanceledException) when (test.IsCancellationRequested)
        {
            // Closing the dialog cancels the current test.
        }
        finally
        {
            connectionTest = null;
            if (!test.IsCancellationRequested)
            {
                SetTestInProgress(false);
            }
        }
    }

    private void SaveSettings(object sender, RoutedEventArgs e)
    {
        var value = CurrentConnectionString;
        if (!string.IsNullOrWhiteSpace(value) && !ValidateOptions(value))
        {
            return;
        }

        try
        {
            DatabaseSettings.SaveConnectionString(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            ShowFeedback("接続文字列を保存できませんでした。Windowsのユーザー設定を確認してください。",
                "SystemFillColorCriticalBrush");
            return;
        }

        DialogResult = true;
    }

    private bool ValidateOptions(string value)
    {
        if (DatabaseConnectionTest.HasValidOptions(value))
        {
            return true;
        }

        ShowFeedback("MySQL・MariaDBの接続文字列を「項目名=値;」の形式で入力してください。",
            "SystemFillColorCriticalBrush");
        return false;
    }

    private void SetTestInProgress(bool inProgress)
    {
        ConnectionInput.IsEnabled = !inProgress;
        TestButton.IsEnabled = !inProgress;
        SaveButton.IsEnabled = !inProgress;
    }

    private void ShowFeedback(string message, string foregroundResource)
    {
        ConnectionFeedback.Text = message;
        ConnectionFeedback.SetResourceReference(TextBlock.ForegroundProperty, foregroundResource);
        ConnectionFeedback.Visibility = Visibility.Visible;
    }

    protected override void OnClosed(EventArgs e)
    {
        connectionTest?.Cancel();
        base.OnClosed(e);
    }
}
