using AwesomeAssertions;

using Earmark.Core.Models;
using Earmark.Core.Routing;

using Xunit;

namespace Earmark.Core.Tests;

public class PatternMatcherTests
{
    // ---- Regex (default, unchanged) ----

    [Fact]
    public void Regex_matches_like_before()
    {
        PatternMatcher.Matches(PatternMatchMode.Regex, ".*Head.*", "Sony Headphones").Should().BeTrue();
        PatternMatcher.Matches(PatternMatchMode.Regex, "^Sony$", "Sony Headphones").Should().BeFalse();
        PatternMatcher.Matches(PatternMatchMode.Regex, "Sony Headphones", "Sony Headphones").Should().BeTrue();
    }

    [Fact]
    public void Invalid_regex_never_matches()
    {
        PatternMatcher.Matches(PatternMatchMode.Regex, "(unclosed", "anything").Should().BeFalse();
    }

    // ---- Wildcard (contains; * = run, ? = one; backslash escapes) ----

    [Theory]
    [InlineData("Sony", "Sony Headphones (4-WH-1000XM5)", true)]   // bare word = contains
    [InlineData("Sony*phones", "Sony Headphones", true)]
    [InlineData("Sony*phones", "Sony Earbuds", false)]
    [InlineData("XM?", "WH-1000XM5", true)]                        // ? = exactly one char
    [InlineData("XM?", "WH-1000XM", false)]
    [InlineData("Speakers", "Headphones", false)]
    public void Wildcard_matches_as_contains(string pattern, string candidate, bool expected)
    {
        PatternMatcher.Matches(PatternMatchMode.Wildcard, pattern, candidate).Should().Be(expected);
    }

    [Fact]
    public void Wildcard_backslash_escapes_star_and_question()
    {
        // "\*" is a literal asterisk, not "any run".
        PatternMatcher.Matches(PatternMatchMode.Wildcard, @"Mix \*", "Mix * channel").Should().BeTrue();
        PatternMatcher.Matches(PatternMatchMode.Wildcard, @"Mix \*", "Mix A channel").Should().BeFalse();
        // "\?" is a literal question mark.
        PatternMatcher.Matches(PatternMatchMode.Wildcard, @"What\?", "What? device").Should().BeTrue();
        PatternMatcher.Matches(PatternMatchMode.Wildcard, @"What\?", "Whatx device").Should().BeFalse();
    }

    [Fact]
    public void Wildcard_treats_regex_metachars_literally()
    {
        // A device name with parens is matched literally in wildcard mode (no regex grouping).
        PatternMatcher.Matches(PatternMatchMode.Wildcard, "(4-WH-1000XM5)", "Sony (4-WH-1000XM5)").Should().BeTrue();
    }

    // ---- Exact (case-insensitive, whole string) ----

    [Fact]
    public void Exact_matches_whole_string_case_insensitively()
    {
        PatternMatcher.Matches(PatternMatchMode.Exact, "Sony Headphones (4-WH-1000XM5)", "Sony Headphones (4-WH-1000XM5)").Should().BeTrue();
        PatternMatcher.Matches(PatternMatchMode.Exact, "sony headphones (4-wh-1000xm5)", "Sony Headphones (4-WH-1000XM5)").Should().BeTrue();
        // Not a substring match.
        PatternMatcher.Matches(PatternMatchMode.Exact, "Sony", "Sony Headphones").Should().BeFalse();
        // A regex metachar is literal in exact mode.
        PatternMatcher.Matches(PatternMatchMode.Exact, ".*", "anything").Should().BeFalse();
    }
}
