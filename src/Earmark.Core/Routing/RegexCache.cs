using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Earmark.Core.Routing;

internal static class RegexCache
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    private const RegexOptions Options =
        RegexOptions.Compiled |
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase;

    public static Regex Get(string pattern) =>
        Cache.GetOrAdd(pattern, static p => new Regex(p, Options, TimeSpan.FromMilliseconds(250)));

    public static bool TryGet(string pattern, out Regex? regex)
    {
        try
        {
            regex = Get(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            regex = null;
            return false;
        }
    }
}
