Currently a majority of the rules act as 'pins'
e.g. Change volume forces that volume, mute forces mute

And our application reconciles external changes to match that pin (i.e. a user forces mute in earmark, then unmutes from settings, earmark will instantly re-mute the device)

This is expected.

However specifically for condition based rules, I want to add a toggle to these kinds of actions "Pin" - which will perform the current behavior if conditions are met.
The new functionality is when Pin is disabled, then it's a one shot operation when the conditions change - no reconciliation.

This goes for most rule types...
e.g:
 -  Add wavelink device to mix is actually Ensure wavelink device is in mix, add to mix is the one-shot version
 -  Remove wavelink device from mix is actually Ensure wavelink device is NOT in mix, Remove from mix is the one-shot version
 - Volume, mute, set output, etc. All rules have a one-shot vs pinned version

Additionally combine Mute/Unmute device with an in-rule toggle - see which other rules we could combine in the same way
