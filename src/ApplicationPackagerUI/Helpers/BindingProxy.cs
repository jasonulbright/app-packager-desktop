using System.Windows;

namespace ApplicationPackagerUI.Helpers;

/// <summary>
/// Carries a data context into a resource. A DataGrid column is not in the visual tree, so a binding
/// on one of its properties finds no data context without it.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
