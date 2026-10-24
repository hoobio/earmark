using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace Earmark.Desktop.Services;

public sealed class DialogCommandButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    internal bool IsAutomationInvoke { get; private set; }

    protected override AutomationPeer OnCreateAutomationPeer() => new DialogCommandButtonAutomationPeer(this);

    private sealed class DialogCommandButtonAutomationPeer(DialogCommandButton owner) : ButtonAutomationPeer(owner), IInvokeProvider
    {
        void IInvokeProvider.Invoke()
        {
            owner.IsAutomationInvoke = true;
            try
            {
                Invoke();
            }
            finally
            {
                owner.IsAutomationInvoke = false;
            }
        }
    }
}
