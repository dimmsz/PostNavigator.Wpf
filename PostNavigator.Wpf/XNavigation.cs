using System.Globalization;
using System.Text.RegularExpressions;

namespace PostNavigator.Wpf;

internal static partial class XNavigation
{
    public const int FirstYear = 2006;
    public static Uri Home { get; } = new("https://x.com/");
    public static Uri SignIn { get; } = new(Home, "i/flow/login");

    public static bool IsXPage(string? address)
    {
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps && IsProfileHost(uri.Host);
    }

    public static bool TryGetAccount(string input, out string account)
    {
        account = input.Trim();
        if (Uri.TryCreate(account, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
            IsProfileHost(uri.Host))
        {
            account = uri.AbsolutePath.Trim('/');
        }
        else if (account.StartsWith('@'))
        {
            account = account[1..];
        }

        return AccountPattern().IsMatch(account);
    }

    public static Uri Profile(string account, bool includeReplies)
    {
        return new Uri(Home, includeReplies ? $"{account}/with_replies" : account);
    }

    public static (DateOnly Start, DateOnly End) DateRange(int year, int month)
    {
        if (month == 0)
        {
            var start = new DateOnly(year, 1, 1);
            return (start, start.AddYears(1));
        }

        var firstDay = new DateOnly(year, month, 1);
        return (firstDay, firstDay.AddMonths(1));
    }

    public static Uri Search(string account, DateOnly start, DateOnly end)
    {
        var query = string.Create(CultureInfo.InvariantCulture,
            $"from:{account} since:{start:yyyy-MM-dd} until:{end:yyyy-MM-dd}");
        return new Uri(Home, $"search?q={Uri.EscapeDataString(query)}&src=typed_query&f=live");
    }

    private static bool IsProfileHost(string host)
    {
        return host is "x.com" or "www.x.com" or "twitter.com" or "www.twitter.com";
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_]{1,15}\z")]
    private static partial Regex AccountPattern();
}
