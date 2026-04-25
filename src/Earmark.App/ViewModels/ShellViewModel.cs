using CommunityToolkit.Mvvm.ComponentModel;

namespace Earmark.App.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; }

    public ShellViewModel() => Title = "Earmark";
}
