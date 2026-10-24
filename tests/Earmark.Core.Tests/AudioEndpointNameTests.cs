using AwesomeAssertions;

using Earmark.Core.Models;

using Xunit;

namespace Earmark.Core.Tests;

public class AudioEndpointNameTests
{
    [Theory]
    [InlineData("WA7 Fireflies (2- USB2.0 High-Speed True HD Audio)", "2- USB2.0 High-Speed True HD Audio", "WA7 Fireflies", "WA7 Fireflies (2- USB2.0 High-Speed True HD Audio)")]
    [InlineData("Chat/Mic (Clips) (Elgato Virtual Audio)", "Elgato Virtual Audio", "Chat/Mic (Clips)", "Chat/Mic (Clips) (Elgato Virtual Audio)")]
    [InlineData("Speakers", "Realtek Audio", "Speakers", "Speakers (Realtek Audio)")]
    [InlineData("Speakers", "", "Speakers", "Speakers")]
    [InlineData("(Elgato Virtual Audio)", "Elgato Virtual Audio", "(Elgato Virtual Audio)", "(Elgato Virtual Audio)")]
    public void Name_strips_the_description_suffix_and_picker_name_never_repeats_it(string friendly, string description, string name, string pickerName)
    {
        var endpoint = new AudioEndpoint("id", friendly, description, EndpointFlow.Render, EndpointState.Active, false, false);

        endpoint.Name.Should().Be(name);
        endpoint.PickerName.Should().Be(pickerName);
        endpoint.FriendlyName.Should().Be(friendly);
    }
}
