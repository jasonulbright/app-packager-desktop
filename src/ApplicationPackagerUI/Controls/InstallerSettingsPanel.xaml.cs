using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerUI.Controls;

/// <summary>
/// Dynamically renders InstallerParameter[] as WPF-UI controls grouped by Group field.
/// Each parameter renders as its ControlType (Toggle, Dropdown, Text, Number) with a tooltip.
/// Values are stored back into a dictionary for the engine to resolve into InstallArgs.
/// </summary>
public partial class InstallerSettingsPanel : UserControl
{
    /// <summary>Current parameter values keyed by parameter Key.</summary>
    public Dictionary<string, string> Values { get; } = new();

    public static readonly DependencyProperty ParametersProperty =
        DependencyProperty.Register(nameof(Parameters), typeof(InstallerParameter[]),
            typeof(InstallerSettingsPanel), new PropertyMetadata(null, OnParametersChanged));

    public InstallerParameter[]? Parameters
    {
        get => (InstallerParameter[]?)GetValue(ParametersProperty);
        set => SetValue(ParametersProperty, value);
    }

    public InstallerSettingsPanel()
    {
        InitializeComponent();
    }

    private static void OnParametersChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is InstallerSettingsPanel panel)
            panel.RenderParameters();
    }

    private void RenderParameters()
    {
        RootPanel.Children.Clear();
        Values.Clear();

        var parameters = Parameters;
        if (parameters is null || parameters.Length == 0)
            return;

        // Group parameters
        var groups = parameters.GroupBy(p => p.Group ?? "General").OrderBy(g => g.Key);

        foreach (var group in groups)
        {
            var expander = new Wpf.Ui.Controls.CardExpander
            {
                Header = group.Key,
                IsExpanded = true,
                Margin = new Thickness(0, 4, 0, 4)
            };

            var stackPanel = new StackPanel { Margin = new Thickness(8) };

            foreach (var param in group)
            {
                Values[param.Key] = param.DefaultValue;

                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };

                var label = new System.Windows.Controls.TextBlock
                {
                    Text = param.Label,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0)
                };
                if (!string.IsNullOrEmpty(param.Tooltip))
                    label.ToolTip = param.Tooltip;
                DockPanel.SetDock(label, Dock.Left);
                row.Children.Add(label);

                FrameworkElement control = param.ControlType switch
                {
                    "Toggle" => CreateToggle(param),
                    "Dropdown" => CreateDropdown(param),
                    "Text" => CreateTextBox(param),
                    "Number" => CreateNumberBox(param),
                    _ => CreateTextBox(param)
                };

                control.HorizontalAlignment = HorizontalAlignment.Right;
                if (!string.IsNullOrEmpty(param.Tooltip))
                    control.ToolTip = param.Tooltip;
                DockPanel.SetDock(control, Dock.Right);
                row.Children.Add(control);

                stackPanel.Children.Add(row);
            }

            expander.Content = stackPanel;
            RootPanel.Children.Add(expander);
        }
    }

    private ToggleSwitch CreateToggle(InstallerParameter param)
    {
        var toggle = new ToggleSwitch
        {
            IsChecked = param.DefaultValue.Equals("true", StringComparison.OrdinalIgnoreCase)
        };
        toggle.Checked += (_, _) => Values[param.Key] = "true";
        toggle.Unchecked += (_, _) => Values[param.Key] = "false";
        return toggle;
    }

    private ComboBox CreateDropdown(InstallerParameter param)
    {
        var combo = new ComboBox { Width = 200 };
        if (param.Options is not null)
        {
            foreach (var opt in param.Options)
                combo.Items.Add(opt);
            combo.SelectedItem = param.DefaultValue;
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string val)
                Values[param.Key] = val;
        };
        return combo;
    }

    private Wpf.Ui.Controls.TextBox CreateTextBox(InstallerParameter param)
    {
        var textBox = new Wpf.Ui.Controls.TextBox
        {
            Text = param.DefaultValue,
            Width = 200
        };
        textBox.TextChanged += (_, _) => Values[param.Key] = textBox.Text;
        return textBox;
    }

    private Wpf.Ui.Controls.NumberBox CreateNumberBox(InstallerParameter param)
    {
        var numberBox = new Wpf.Ui.Controls.NumberBox
        {
            Value = double.TryParse(param.DefaultValue, out var v) ? v : 0,
            Minimum = param.Minimum ?? 0,
            Maximum = param.Maximum ?? 99999,
            SpinButtonPlacementMode = Wpf.Ui.Controls.NumberBoxSpinButtonPlacementMode.Compact,
            Width = 140
        };
        numberBox.ValueChanged += (_, _) => Values[param.Key] = (numberBox.Value ?? 0).ToString();
        return numberBox;
    }
}
