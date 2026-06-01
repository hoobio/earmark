using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Earmark.Core.Routing;

public static class RegexCache
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Regex> WildcardCache = new(StringComparer.Ordinal);

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

    /// <summary>The compiled regex for a wildcard (glob) pattern, cached. Always valid (the
    /// conversion only ever emits a well-formed, unanchored regex), so it never returns null.</summary>
    public static Regex GetWildcard(string pattern) =>
        WildcardCache.GetOrAdd(pattern, static p => new Regex(WildcardToRegex(p), Options, TimeSpan.FromMilliseconds(250)));

    /// <summary>
    /// Glob -> regex: <c>*</c> = any run, <c>?</c> = one char, <c>\*</c> / <c>\?</c> (and any other
    /// <c>\x</c>) = the literal char. Unanchored, so the result matches as "contains".
    /// </summary>
    internal static string WildcardToRegex(string pattern)
    {
        var sb = new StringBuilder(pattern.Length * 2);
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
            {
                sb.Append(Regex.Escape(pattern[i + 1].ToString()));
                i++;
            }
            else if (c == '*')
            {
                sb.Append(".*");
            }
            else if (c == '?')
            {
                sb.Append('.');
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        return sb.ToString();
    }
}
