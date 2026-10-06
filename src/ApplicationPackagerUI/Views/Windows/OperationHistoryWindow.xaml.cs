using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Views.Windows;

public partial class OperationHistoryWindow
{
    public ObservableCollection<InventoryOperationState> Operations { get; } = [];
    public ObservableCollection<InventoryEventRecord> Events { get; } = [];

    private readonly ApplicationInventoryService _inventory;
    private readonly string _packagerUid;

    public OperationHistoryWindow(PackagerInfo packager, ApplicationInventoryService inventory)
    {
        ArgumentNullException.ThrowIfNull(packager);
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _packagerUid = packager.Uid;

        InitializeComponent();
        DataContext = this;
        Title = $"Operation history — {packager.Application}";

        foreach (var operation in _inventory.GetOperations(_packagerUid, 100))
            Operations.Add(operation);

        EmptyHistoryText.Visibility = Operations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Operations.Count > 0)
            OperationsGrid.SelectedIndex = 0;
    }

    private void OperationsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Events.Clear();
        if (OperationsGrid.SelectedItem is InventoryOperationState operation)
        {
            foreach (var item in _inventory.GetEventsForOperation(operation.OperationId))
                Events.Add(item);
        }

        EmptyEventsText.Text = Events.Count == 0
            ? OperationsGrid.SelectedItem is null
                ? "Select an operation to see its recorded events."
                : "No detailed events were recorded for this operation."
            : string.Empty;
        EmptyEventsText.Visibility = Events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
