using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using ApplicationPackagerUI.ViewModels.Windows;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace ApplicationPackagerUI.Views.Windows;

public partial class PublishResultsWindow
{
    /// <summary>What a row is higher than the content of its cells: the edge of a cell, and the line under the row.</summary>
    private const double RowEdges = 3;

    public PublishResultsWindow(PublishResultsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Controls.Mock.DialogFrame.Attach(this);
        BuildColumns(viewModel);
        // The rows of the two tables with several lines are half a unit higher in the mock.
        if (viewModel.IsOneClick || viewModel.IsMatrix) Matrix.MinRowHeight = 35;
        // The sizes of the three windows of the mock. A longer list makes the window taller, up to the height of the work area.
        var (width, height, rows) = viewModel.IsOneClick ? (1290d, 550d, 3) : viewModel.IsMatrix ? (1350d, 495d, 2) : (1190d, 440d, 1);
        Width = width;
        Controls.Mock.DialogFrame.RoundAs(this, height);
        // The rows of the mock are a fraction of a pixel high: its first row rounds up, and in the matrix the second one rounds down.
        Matrix.LoadingRow += (_, e) =>
        {
            var index = e.Row.GetIndex();
            // So do the texts: the name, and a cell with a glyph, stand above the middle of their row.
            var several = viewModel.IsOneClick || viewModel.IsMatrix;
            static System.Windows.Media.TranslateTransform Down(int units) => new(0, units);
            e.Row.Resources["MarkShift"] = Down(several && index == 0 ? -1 : 0);
            e.Row.Resources["NameShift"] = Down(several && index < 2 ? -1 : 0);
            e.Row.Resources["SignalShift"] = Down(several && index == 0 ? -2 : -1);
            e.Row.Resources["GlyphShift"] = Down(several && index == 1 ? -1 : 0);
            e.Row.Resources["DashShift"] = Down(several && index == 0 ? 0 : 1);
            // A message of the Result column can wrap in a row of any state, so such a table has no fixed row height: its first row has a
            // least height, which the content of its first cell gives it, because the grid sets the least height of a row itself.
            e.Row.Resources["RowContent"] = !viewModel.IsMatrix && index == 0 ? (viewModel.IsOneClick ? 37d : 36d) - RowEdges : 0d;
            // A row that the grid uses again keeps no height of its last place. No cell of several systems wraps, so those rows have the
            // heights of the mock.
            e.Row.ClearValue(FrameworkElement.HeightProperty);
            if (viewModel.IsMatrix && index == 0) e.Row.Height = 37;
            else if (viewModel.IsMatrix && index == 1) e.Row.Height = 35;
        };
        if (viewModel.IsMatrix)
        {
            Controls.Mock.Mk.SetDrop(pnlIssue, 1);
            Controls.Mock.Mk.SetDrop(txtFailureHelp, 1);
            foreach (var button in new[] { btnResultsHistory, btnResultsReport, btnResultsLogs, btnResultsDone }) Controls.Mock.Mk.SetTextDrop(button, -1);
            // Two buttons of the foot of the mock differ by a unit from the width of their text.
            btnResultsReport.Padding = new Thickness(11, 0, 12, 0);
            btnResultsLogs.Padding = new Thickness(10, 0, 11, 0);
        }
        Height = Math.Min(height + Math.Max(0, viewModel.Rows.Count - rows) * 36, Math.Max(height, SystemParameters.WorkArea.Height - 40));
        Controls.Mock.DialogFrame.KeepInWorkArea(this, BodyScroll);
        // The mock draws the window of several systems without these lines: the amber notice and the cells name the failure. The row menu shows them.
        if (viewModel.IsMatrix) lstFailures.Visibility = Visibility.Collapsed;
        btnResultsHistory.Visibility = viewModel.HasHistory && !viewModel.IsOneClick ? Visibility.Visible : Visibility.Collapsed;
        // The table says what its menu has, also where the window has no line for it.
        if (viewModel.RowMenuTip.Length > 0)
        {
            Matrix.ToolTip = viewModel.RowMenuTip;
            System.Windows.Automation.AutomationProperties.SetHelpText(Matrix, viewModel.RowMenuTip);
        }
        // The button of the mock stands on a One Click run. The other results offer it in the row menu.
        btnResultsReview.Visibility = viewModel.IsOneClick && viewModel.HasFailure ? Visibility.Visible : Visibility.Collapsed;
    }

    private PublishResultsViewModel Results => (PublishResultsViewModel)DataContext;

    private void BuildColumns(PublishResultsViewModel viewModel)
    {
        // The column widths of the mock, as star weights.
        var (application, version, systems, result) = viewModel.IsOneClick ? (215.1, 83.2, new[] { 177.0, 109.9, 106.7 }, 558.0)
            : viewModel.IsMatrix ? (266.1, 159.5, new[] { 234.7, 309.1, 340.6 }, 0d)
            : (215.0, 93.6, new[] { 199.2, 123.7, 120.0 }, 398.4);

        Matrix.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Application", Width = Star(application), MinWidth = 160, SortMemberPath = "Application",
            CellTemplate = Template("""
                <DockPanel Background="Transparent" ToolTip="{Binding Tip}" MinHeight="{DynamicResource RowContent}">
                    <Border RenderTransform="{DynamicResource MarkShift}" DockPanel.Dock="Left" Width="22" Height="22" CornerRadius="4" BorderThickness="1" VerticalAlignment="Center" Margin="0,1,9,-1"
                            Background="{DynamicResource MkSubBrush}" BorderBrush="{DynamicResource MkEdgeBrush}">
                        <TextBlock Text="{Binding Mark}" FontSize="9" FontWeight="Bold" HorizontalAlignment="Center" VerticalAlignment="Center" Margin="1,0,0,0"
                                   Foreground="{DynamicResource MkTextBrush}" />
                    </Border>
                    <TextBlock Text="{Binding Application}" FontWeight="Bold" VerticalAlignment="Center" Margin="0,-1,0,0" TextTrimming="CharacterEllipsis"
                               RenderTransform="{DynamicResource NameShift}" />
                </DockPanel>
                """)
        });
        Matrix.Columns.Add(TextColumn(viewModel.IsMatrix ? "Latest version" : "Version", "Version", version, 80));
        for (var i = 0; i < viewModel.Columns.Count; i++)
        {
            Matrix.Columns.Add(new DataGridTemplateColumn
            {
                Header = viewModel.Columns[i].Header, Width = Star(systems[Math.Min(i, systems.Length - 1)]), MinWidth = 100, SortMemberPath = $"Cells[{i}].Text",
                CellStyle = PublishMatrixColumns.Named((Style)FindResource("Mk.GridCell"), $"Names[{i}]"),
                CellTemplate = Template($$"""
                    <DockPanel Background="Transparent" ToolTip="{Binding Tip}">
                        <DockPanel.Style>
                            <Style TargetType="DockPanel">
                                <Setter Property="RenderTransform" Value="{DynamicResource SignalShift}" />
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding Cells[{{i}}].Icon}" Value="">
                                        <Setter Property="RenderTransform" Value="{DynamicResource DashShift}" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </DockPanel.Style>
                        <mk:Ico DockPanel.Dock="Left" Margin="0,0,6,0" RenderTransform="{DynamicResource GlyphShift}" Glyph="{Binding Cells[{{i}}].Icon}" Foreground="{Binding Foreground, ElementName=Words}">
                            <mk:Ico.Style>
                                <Style TargetType="mk:Ico" BasedOn="{x:Static mk:Ico.BaseStyle}">
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding Cells[{{i}}].Icon}" Value="">
                                            <Setter Property="Visibility" Value="Collapsed" />
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </mk:Ico.Style>
                        </mk:Ico>
                        <TextBlock x:Name="Words" Text="{Binding Cells[{{i}}].Text}" Tag="{Binding Cells[{{i}}].Kind}" VerticalAlignment="Center" Margin="0,-1,0,0"
                                   Style="{DynamicResource ResultSignalText}" />
                    </DockPanel>
                    """)
            });
        }
        if (result > 0)
        {
            var words = TextColumn(viewModel.IsOneClick ? "Details" : "Result", "Result", result, 200);
            // A message that is longer than the column wraps, so all of it shows.
            var wrap = new Style(typeof(System.Windows.Controls.TextBlock), words.ElementStyle);
            wrap.Setters.Add(new Setter(System.Windows.Controls.TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            words.ElementStyle = wrap;
            Matrix.Columns.Add(words);
        }
        Matrix.Resources["ResultSignalText"] = FindResource("SignalText");
    }

    private static DataGridLength Star(double weight) => new(weight, DataGridLengthUnitType.Star);

    private DataGridTextColumn TextColumn(string header, string path, double weight, double minimum) => new()
    {
        Header = header, Binding = new System.Windows.Data.Binding(path), Width = Star(weight), MinWidth = minimum,
        ElementStyle = (Style)FindResource("Mk.CellText")
    };

    private static DataTemplate Template(string body) => (DataTemplate)XamlReader.Parse(
        "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" " +
        "xmlns:mk=\"clr-namespace:ApplicationPackagerUI.Controls.Mock;assembly=ApplicationPackagerUI\">" + body + "</DataTemplate>");

    /// <summary>
    /// The menu of a result row: a retry of each failed system, the review of the failed application, the report and the history. A retry
    /// that the app cannot verify is shown with what to verify by hand.
    /// </summary>
    public ContextMenu BuildRowMenu(ResultLine? line)
    {
        var menu = new ContextMenu();
        FillRowMenu(menu, line);
        return menu;
    }

    private void RowMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu) FillRowMenu(menu, Matrix.SelectedItem as ResultLine);
    }

    private void FillRowMenu(ContextMenu menu, ResultLine? line)
    {
        menu.Items.Clear();
        foreach (var offer in Results.Retries.Where(o => line is null || o.Uid == line.Uid))
        {
            var item = new MenuItem
            {
                Header = offer.CanRetry ? offer.ButtonName : offer.Text,
                IsEnabled = offer.CanRetry,
                ToolTip = offer.CanRetry ? offer.Text + " Opens the publish review for this application and this system only. Nothing is published until you confirm." : offer.Text,
            };
            ToolTipService.SetShowOnDisabled(item, true);
            System.Windows.Automation.AutomationProperties.SetAutomationId(item, "btnResultsRetry");
            item.Click += (_, _) => Retry(offer);
            menu.Items.Add(item);
        }
        if (Results.HasFailure) menu.Items.Add(Command("Review failed application", "miResultsReview", () => Review_Click(this, new RoutedEventArgs())));
        if (Results.IsMatrix && Results.FailureLines.Count > 0)
            menu.Items.Add(Command("Show failure messages", "miResultsFailureMessages", () => lstFailures.ClearValue(VisibilityProperty)));
        if (Results.HasHistory) menu.Items.Add(Command("Operation history", "miResultsHistory", () => History_Click(this, new RoutedEventArgs())));
        if (Results.HasReport) menu.Items.Add(Command("Open report", "miResultsReport", () => Report_Click(this, new RoutedEventArgs())));
    }

    private static MenuItem Command(string header, string id, Action action)
    {
        var item = new MenuItem { Header = header };
        System.Windows.Automation.AutomationProperties.SetAutomationId(item, id);
        item.Click += (_, _) => action();
        return item;
    }

    private void Retry(PublishRetryOffer offer)
    {
        Results.RequestRetry(offer);
        Close();
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        var uid = (Matrix.SelectedItem as ResultLine)?.Uid ?? Results.FirstFailed?.Uid ?? Results.Rows.FirstOrDefault()?.Uid;
        if (uid is not null) Results.ShowHistory?.Invoke(uid, this);
    }

    private void RunHistory_Click(object sender, RoutedEventArgs e) => Results.ShowRunHistory?.Invoke(this);

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        if (!System.IO.File.Exists(Results.ReportPath)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Results.ReportPath) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private void Logs_Click(object sender, RoutedEventArgs e) => Results.ShowLogs?.Invoke(this);

    private void Review_Click(object sender, RoutedEventArgs e)
    {
        if (Results.FirstFailed is not { } row) return;
        Results.RequestReview(row);
        Close();
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
