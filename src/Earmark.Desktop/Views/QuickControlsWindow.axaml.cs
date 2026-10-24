using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

using Earmark.Desktop.Services;

namespace Earmark.Desktop.Views;

public sealed partial class QuickControlsWindow : Window
{
    private const int EdgeMargin = 8;

    public QuickControlsWindow(ShellSettings settings)
    {
        InitializeComponent();
        GroundGlow.IsVisible = settings.UsesMockMica;
        Deactivated += (_, _) => Hide();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Hide();
            }
        };
        Closing += (_, e) =>
        {
            if (e.CloseReason == WindowCloseReason.WindowClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    public void ShowNearTray()
    {
        if (Screens.Primary is { } screen)
        {
            var area = screen.WorkingArea;
            var size = PixelSize.FromSize(new Size(Width, Height), screen.Scaling);
            Position = new PixelPoint(area.Right - size.Width - (int)(EdgeMargin * screen.Scaling), area.Bottom - size.Height - (int)(EdgeMargin * screen.Scaling));
        }

        Show();
        Activate();
        Native.ForceForeground(TryGetPlatformHandle()?.Handle ?? 0);
    }
}
