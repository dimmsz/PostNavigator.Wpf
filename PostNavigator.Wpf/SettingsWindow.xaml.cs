using System.Data.Common;
using System.IO;
using System.Security.Cryptography;
using System.Windows;

namespace PostNavigator.Wpf;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        try
        {
            MaskedConnectionString.Password = DatabaseSettings.LoadConnectionString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            ShowError("保存済みの接続文字列を読み込めませんでした。接続文字列を入力して保存し直せます。");
        }
    }

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

    private void SaveSettings(object sender, RoutedEventArgs e)
    {
        var connectionString = RevealConnectionString.IsChecked == true
            ? VisibleConnectionString.Text
            : MaskedConnectionString.Password;

        if (!string.IsNullOrWhiteSpace(connectionString) && !HasConnectionStringSyntax(connectionString))
        {
            ShowError("接続文字列を「項目名=値;」の形式で入力してください。");
            return;
        }

        try
        {
            DatabaseSettings.SaveConnectionString(connectionString);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            ShowError("接続文字列を保存できませんでした。Windowsのユーザー設定を確認してください。");
            return;
        }

        DialogResult = true;
    }

    private static bool HasConnectionStringSyntax(string connectionString)
    {
        try
        {
            return new DbConnectionStringBuilder { ConnectionString = connectionString }.Count > 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void ShowError(string message)
    {
        ErrorMessage.Text = message;
        ErrorMessage.Visibility = Visibility.Visible;
    }
}
