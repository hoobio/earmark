namespace Earmark.Desktop.Services;

public sealed class QuickControlsVisibility
{
    public event EventHandler? Changed;

    public bool IsVisible
    {
        get;
        internal set
        {
            if (field != value)
            {
                field = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
