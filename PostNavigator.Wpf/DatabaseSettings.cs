using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PostNavigator.Wpf;

internal static class DatabaseSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PostNavigator", "database-connection.dat");

    public static string LoadConnectionString()
    {
        byte[] encrypted;
        try
        {
            encrypted = File.ReadAllBytes(SettingsPath);
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return string.Empty;
        }

        var plaintext = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static void SaveConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            try
            {
                File.Delete(SettingsPath);
            }
            catch (DirectoryNotFoundException)
            {
                // There are no saved database settings.
            }
            return;
        }

        var plaintext = Encoding.UTF8.GetBytes(connectionString);
        byte[] encrypted;
        try
        {
            encrypted = ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".database-connection-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, encrypted);
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
