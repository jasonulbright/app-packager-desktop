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
    private Dictionary<string, string> _values = new();

    /// <summary>
    /// Current parameter values keyed by parameter Key. This is the Store when one was
    /// supplied, so a caller that owns the dictionary sees edits without copying.
    /// </summary>
    public Dictionary<string, string> Values => _values;

    public static readonly DependencyProperty ParametersProperty =
        DependencyProperty.Register(nameof(Parameters), typeof(IEnumerable<InstallerParameter>),
            typeof(InstallerSettingsPanel), new PropertyMetadata(null, OnParametersChanged));

    public IEnumerable<InstallerParameter>? Parameters
    {
        get => (IEnumerable<InstallerParameter>?)GetValue(ParametersProperty);
        set => SetValue(ParametersProperty, value);
    }

    /// <summary>
    /// Dictionary the panel reads its starting values from and writes edits into. A key
    /// missing from it starts at the parameter's default and is written on render, so the
    /// store always holds every parameter after the panel has shown.
    /// </summary>
    public static readonly DependencyProperty StoreProperty =
        DependencyProperty.Register(nameof(Store), typeof(Dictionary<string, string>),
            typeof(InstallerSettingsPanel), new PropertyMetadata(null, OnParametersChanged));

    public Dictionary<string, string>? Store
    {
        get => (Dictionary<string, string>?)GetValue(StoreProperty);
        set => SetValue(StoreProperty, value);
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
        _values = Store ?? new Dictionary<string, string>();

        var parameters = Parameters?.ToArray();
        if (parameters is null || parameters.Length == 0)
            return;

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
                if (!_values.TryGetValue(param.Key, out var current))
                {
                    current = param.DefaultValue;
                    _values[param.Key] = current;
                }

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

                if (param.ControlType == ApplicationPackagerEngine.Services.StagingService.LocaleListControl)
                {
                    var list = CreateLocaleList(param, current);
                    if (!string.IsNullOrEmpty(param.Tooltip)) list.ToolTip = param.Tooltip;
                    var block = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
                    label.Margin = new Thickness(0, 0, 0, 4);
                    row.Children.Remove(label);
                    block.Children.Add(label);
                    block.Children.Add(list);
                    stackPanel.Children.Add(block);
                    continue;
                }

                FrameworkElement control = param.ControlType switch
                {
                    "Toggle" => CreateToggle(param, current),
                    "Dropdown" => CreateDropdown(param, current),
                    "Text" => CreateTextBox(param, current),
                    "Number" => CreateNumberBox(param, current),
                    _ => CreateTextBox(param, current)
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

    private ToggleSwitch CreateToggle(InstallerParameter param, string current)
    {
        var toggle = new ToggleSwitch
        {
            IsChecked = current.Equals("true", StringComparison.OrdinalIgnoreCase)
        };
        toggle.Checked += (_, _) => _values[param.Key] = "true";
        toggle.Unchecked += (_, _) => _values[param.Key] = "false";
        return toggle;
    }

    private ComboBox CreateDropdown(InstallerParameter param, string current)
    {
        var combo = new ComboBox { Width = 200 };
        if (param.Options is not null)
        {
            foreach (var opt in param.Options)
                combo.Items.Add(opt);
            combo.SelectedItem = param.Options.Contains(current) ? current : param.DefaultValue;
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string val)
                _values[param.Key] = val;
        };
        return combo;
    }

    private Wpf.Ui.Controls.TextBox CreateTextBox(InstallerParameter param, string current)
    {
        var textBox = new Wpf.Ui.Controls.TextBox
        {
            Text = current,
            Width = 240
        };
        textBox.TextChanged += (_, _) => _values[param.Key] = textBox.Text;
        return textBox;
    }

    /// <summary>
    /// An All checkbox and one checkbox per locale code. Each code is shown through a
    /// TextBlock: string content reads '_' as an access-key marker and shows de_DE as deDE.
    /// The stored value is All, the checked codes comma separated, or empty.
    /// </summary>
    private StackPanel CreateLocaleList(InstallerParameter param, string current)
    {
        var stored = ApplicationPackagerEngine.Services.StagingService.ParseLocaleList(current);
        var panel = new StackPanel();
        var all = new CheckBox
        {
            Content = new System.Windows.Controls.TextBlock { Text = "All languages" },
            IsChecked = stored.Contains("All"),
            Margin = new Thickness(0, 0, 0, 4)
        };
        System.Windows.Automation.AutomationProperties.SetName(all, "All languages");
        var wrap = new WrapPanel { Margin = new Thickness(16, 2, 0, 4), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
        var boxes = new List<(string Code, CheckBox Box)>();
        foreach (var code in param.Options ?? [])
        {
            var box = new CheckBox
            {
                Content = new System.Windows.Controls.TextBlock { Text = code },
                IsChecked = stored.Contains(code),
                Width = 84,
                Margin = new Thickness(0, 2, 0, 2)
            };
            System.Windows.Automation.AutomationProperties.SetName(box, code);
            boxes.Add((code, box));
            wrap.Children.Add(box);
        }

        void Write()
        {
            wrap.IsEnabled = all.IsChecked != true;
            _values[param.Key] = all.IsChecked == true
                ? "All"
                : string.Join(",", boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Code));
        }
        wrap.IsEnabled = all.IsChecked != true;
        all.Checked += (_, _) => Write();
        all.Unchecked += (_, _) => Write();
        foreach (var (_, box) in boxes)
        {
            box.Checked += (_, _) => Write();
            box.Unchecked += (_, _) => Write();
        }
        panel.Children.Add(all);
        panel.Children.Add(wrap);
        return panel;
    }

    private Wpf.Ui.Controls.NumberBox CreateNumberBox(InstallerParameter param, string current)
    {
        var numberBox = new Wpf.Ui.Controls.NumberBox
        {
            Value = double.TryParse(current, out var v) ? v : double.TryParse(param.DefaultValue, out var d) ? d : 0,
            Minimum = param.Minimum ?? 0,
            Maximum = param.Maximum ?? 99999,
            SpinButtonPlacementMode = Wpf.Ui.Controls.NumberBoxSpinButtonPlacementMode.Compact,
            Width = 140
        };
        numberBox.ValueChanged += (_, _) => _values[param.Key] = (numberBox.Value ?? 0).ToString();
        return numberBox;
    }
}
