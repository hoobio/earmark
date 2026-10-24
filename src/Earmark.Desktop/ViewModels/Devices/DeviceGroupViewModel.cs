using System.Collections.ObjectModel;

namespace Earmark.Desktop.ViewModels.Devices;

public sealed class DeviceGroupViewModel(DevicesViewModel owner, string id, string title) : Observable
{
    private string _title = title;
    private bool _isEditing;

    public DevicesViewModel Owner { get; } = owner;

    public string Id { get; } = id;

    public string Title { get => _title; set => Set(ref _title, value); }

    public bool HasTitle => _title.Length > 0;

    public bool IsEditing { get => _isEditing; set { if (Set(ref _isEditing, value)) Raise(nameof(IsNotEditing)); } }

    public bool IsNotEditing => !IsEditing;

    public ObservableCollection<DeviceCardViewModel> Members { get; } = [];

    public void CommitTitle(string title)
    {
        IsEditing = false;
        title = title.Trim();
        if (title.Length > 0 && title != _title)
        {
            Title = title;
            Raise(nameof(HasTitle));
            Owner.RenameGroup(Id, title);
        }
    }
}
