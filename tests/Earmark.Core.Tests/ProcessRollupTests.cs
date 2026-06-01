using AwesomeAssertions;

using Earmark.Core.Models;

using Xunit;

namespace Earmark.Core.Tests;

public class ProcessRollupTests
{
    [Fact]
    public void Webview_renderer_rolls_up_to_first_non_webview_ancestor()
    {
        var result = ProcessRollup.ResolveHostedOwnerProcessId(300, pid => pid switch
        {
            300 => (200u, "msedgewebview2", @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\msedgewebview2.exe"),
            200 => (100u, "msedgewebview2", @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\msedgewebview2.exe"),
            100 => (50u, "ms-teams", @"C:\Program Files\WindowsApps\MSTeams\ms-teams.exe"),
            _ => default,
        });

        result.Should().Be(100);
    }

    [Fact]
    public void Non_webview_process_keeps_its_own_pid()
    {
        var result = ProcessRollup.ResolveHostedOwnerProcessId(300, pid => pid switch
        {
            300 => (200u, "msedge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"),
            _ => default,
        });

        result.Should().Be(300);
    }

    [Fact]
    public void Missing_owner_info_falls_back_to_original_pid()
    {
        var result = ProcessRollup.ResolveHostedOwnerProcessId(300, pid => pid switch
        {
            300 => (200u, "msedgewebview2", @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\msedgewebview2.exe"),
            200 => default,
            _ => default,
        });

        result.Should().Be(300);
    }
}
