using Earmark.App.ViewModels;

using Microsoft.UI.Xaml.Controls;

namespace Earmark.App.Views;

public sealed partial class RuleEditorDialog : ContentDialog
{
    public RuleEditorDialog()
    {
        InitializeComponent();
    }

    public RuleEditorViewModel Editor { get; set; } = null!;

    private async void OnSaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!Editor.CanSave)
        {
            args.Cancel = true;
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            await Editor.SaveCommand.ExecuteAsync(null);
        }
        finally
        {
            deferral.Complete();
        }
    }
}
