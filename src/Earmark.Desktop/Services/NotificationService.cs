using System.Runtime.InteropServices;

using Avalonia.Platform;

using Earmark.Audio.Services;
using Earmark.Core;
using Earmark.Desktop.Hosting;
using Earmark.Desktop.Views;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

using Windows.UI.Notifications;

namespace Earmark.Desktop.Services;

public sealed class NotificationService(IServiceProvider services, ILogger<NotificationService> logger)
{
    private static readonly string Aumid = AppInfo.ChannelFolder is { Length: > 0 } channel ? $"Hoobi.Earmark.{channel}" : "Hoobi.Earmark";

    private ToastNotifier? _notifier;

    public void Register()
    {
        try
        {
            if (PackageIdentity.IsPackaged)
            {
                _notifier = ToastNotificationManager.CreateToastNotifier();
                return;
            }

            Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(Aumid));
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{Aumid}");
            key.SetValue("DisplayName", AppInfo.IsDevBuild ? "Earmark (Dev)" : "Earmark");
            key.SetValue("IconUri", EnsureIconFile());
            _notifier = ToastNotificationManager.CreateToastNotifier(Aumid);
            logger.LogInformation("Toast notifications registered as {Aumid}", Aumid);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Toast registration failed; notifications will use the in-window toast");
        }
    }

    public void Show(string title, string body)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentNullException.ThrowIfNull(body);
        try
        {
            var toast = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var text = toast.GetElementsByTagName("text");
            text[0].AppendChild(toast.CreateTextNode(title));
            text[1].AppendChild(toast.CreateTextNode(body));
            (_notifier ?? throw new InvalidOperationException("Toast notifier is not registered")).Show(new ToastNotification(toast));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Windows toast failed; using the in-window toast");
            Ui.Post(() => services.GetRequiredService<MainWindow>().ShowToast($"{title}: {body}"));
        }
    }

    private static string EnsureIconFile()
    {
        var path = Path.Combine(Path.GetDirectoryName(ServiceRegistration.LogDirectory)!, "toast-icon.png");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var source = AssetLoader.Open(new Uri("avares://Earmark/Assets/Earmark.png"));
            using var target = File.Create(path);
            source.CopyTo(target);
        }

        return path;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
