using AwesomeAssertions;

using Earmark.Core.Models;

using Xunit;

namespace Earmark.Core.Tests;

public class AudioEndpointNameTests
{
    [Theory]
    [InlineData("WA7 Fireflies (2- USB2.0 High-Speed True HD Audio)", "2- USB2.0 High-Speed True HD Audio", "WA7 Fireflies", "2- USB2.0 High-Speed True HD Audio")]
    [InlineData("Chat/Mic (Clips) (Elgato Virtual Audio)", "Elgato Virtual Audio", "Chat/Mic (Clips)", "Elgato Virtual Audio")]
    [InlineData("Speakers", "Realtek Audio", "Speakers", "Realtek Audio")]
    [InlineData("Speakers", "", "Speakers", null)]
    [InlineData("Speakers", "speakers", "Speakers", null)]
    [InlineData("(Elgato Virtual Audio)", "Elgato Virtual Audio", "(Elgato Virtual Audio)", "Elgato Virtual Audio")]
    public void Name_strips_the_description_suffix_and_subtext_never_repeats_it(string friendly, string description, string name, string? subtext)
    {
        var endpoint = new AudioEndpoint("id", friendly, description, EndpointFlow.Render, EndpointState.Active, false, false);

        endpoint.Name.Should().Be(name);
        endpoint.Subtext.Should().Be(subtext);
        endpoint.FriendlyName.Should().Be(friendly);
    }
}
