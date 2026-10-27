using System.Windows.Input;

namespace ApplicationPackagerUI.Controls;

/// <summary>The Open definition list of the Workbench; its data context is an OpenDefinitionViewModel.</summary>
public partial class OpenDefinitionPanel
{
    public OpenDefinitionPanel() => InitializeComponent();

    /// <summary>A double-click on a row opens it, as the Open button does.</summary>
    public event EventHandler? OpenRequested;

    private void DefinitionGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DefinitionGrid.SelectedItem is not null) OpenRequested?.Invoke(this, EventArgs.Empty);
    }
}
