using Earmark.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Earmark.App.Controls;

/// <summary>
/// Picks the template for a top-level block on the Devices page: the group-container template for a
/// <see cref="DeviceGroupCard"/>, the lone-card template for a <see cref="DeviceCard"/>. Set as the
/// <see cref="ItemsRepeater.ItemTemplate"/> with both templates supplied from page resources.
/// </summary>
public sealed partial class BlockTemplateSelector : DataTemplateSelector
{
    public DataTemplate? CardTemplate { get; set; }

    public DataTemplate? GroupTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is DeviceGroupCard ? GroupTemplate : CardTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
