using Earmark.App.ViewModels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;

namespace Earmark.App.Views;

public sealed partial class RuleEditorDialog : ContentDialog
{
    private readonly ILogger<RuleEditorDialog>? _logger;

    public RuleEditorDialog()
    {
        InitializeComponent();
        try
        {
            _logger = App.Current.Services.GetRequiredService<ILogger<RuleEditorDialog>>();
        }
        catch
        {
            _logger = null;
        }

        _logger?.LogInformation("RuleEditorDialog created");
        Opened += (_, _) => _logger?.LogInformation("RuleEditorDialog opened");
        Closed += (_, args) => _logger?.LogInformation("RuleEditorDialog closed: {Result}", args.Result);
    }

    public RuleEditorViewModel Editor { get; set; } = null!;

    private async void OnSaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _logger?.LogInformation(
            "Save clicked. CanSave={CanSave} App='{App}' Device='{Device}' AppErr={AppErr} DeviceErr={DeviceErr}",
            Editor.CanSave, Editor.AppPattern, Editor.DevicePattern, Editor.AppPatternError, Editor.DevicePatternError);

        if (!Editor.CanSave)
        {
            _logger?.LogWarning("Save no-op: invalid input; closing dialog without saving");
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            await Editor.SaveAsync();
            _logger?.LogInformation("Rule saved");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Save failed");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnCancelClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _logger?.LogInformation("Cancel clicked");
    }
}
