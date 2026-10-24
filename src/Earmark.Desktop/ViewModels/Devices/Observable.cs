using System.Runtime.CompilerServices;

namespace Earmark.Desktop.ViewModels.Devices;

public abstract class Observable : NotifyBase
{
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

    public void RaiseAll() => Raise(string.Empty);
}
