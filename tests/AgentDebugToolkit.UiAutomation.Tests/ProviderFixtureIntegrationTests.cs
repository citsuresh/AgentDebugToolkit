using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AgentDebugToolkit.Mcp;
using AgentDebugToolkit.UiAutomation.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class ProviderFixtureIntegrationTests
{
    private sealed class GridRow
    {
        public GridRow(string label, string value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }
        public string Value { get; }
    }

    [Fact]
    public void BoundedInspectAndVisibleText_SucceedAgainstRealWpfControls()
    {
        using var window = FixtureWindow.Create(() =>
        {
            var panel = new StackPanel();
            for (var index = 0; index < 12; index++)
            {
                var text = new TextBlock { Text = $"Bounded-Text-{index}" };
                AutomationProperties.SetAutomationId(text, $"BoundedText{index}");
                panel.Children.Add(text);
            }

            return panel;
        });

        var inspect = UiCli.Run("inspect", "--hwnd", window.HwndArgument, "--maxNodes", "8", "--maxDurationMs", "120000");
        Assert.True(inspect.Success, inspect.RawOutput);
        Assert.True(inspect.Json.GetProperty("traversal").GetProperty("nodesVisited").GetInt32() <= 8);
        Assert.Contains("node-budget", inspect.Json.GetProperty("traversal").GetProperty("stopReasons").EnumerateArray()
            .Select(reason => reason.GetString()));

        var visibleText = UiCli.Run("read-visible-text", "--hwnd", window.HwndArgument, "--maxNodes", "8", "--maxDurationMs", "120000");
        Assert.True(visibleText.Success, visibleText.RawOutput);
        Assert.Contains("Bounded-Text-0", visibleText.Json.GetProperty("lines").EnumerateArray().Select(line => line.GetString()));
        Assert.True(visibleText.Json.GetProperty("traversal").GetProperty("nodesVisited").GetInt32() <= 8);
    }

    [Fact]
    public void ReadRows_PaginatesRealWpfDataGridAndReturnsCellValues()
    {
        using var window = FixtureWindow.Create(() => CreateDataGrid("RowsGrid", fullRowSelection: false));

        var result = UiCli.Run(
            "read-rows", "--hwnd", window.HwndArgument, "--strategy", "AutomationId", "--value", "RowsGrid",
            "--offset", "1", "--limit", "1", "--maxNodes", "1000", "--maxDurationMs", "120000");

        Assert.True(result.Success, result.RawOutput);
        Assert.Equal(1, result.Json.GetProperty("returned").GetInt32());
        Assert.Equal(2, result.Json.GetProperty("nextOffset").GetInt64());
        var cells = result.Json.GetProperty("rows")[0].GetProperty("cells");
        Assert.True(cells.GetArrayLength() >= 2);
        var cellText = cells.EnumerateArray()
            .Select(cell => cell.TryGetProperty("element", out var element) ? element : default)
            .Where(element => element.ValueKind != System.Text.Json.JsonValueKind.Undefined)
            .SelectMany(element => new[]
            {
                element.TryGetProperty("name", out var name) ? name.GetString() : null,
                element.TryGetProperty("value", out var value) ? value.GetString() : null,
            })
            .Where(text => text is not null)
            .ToArray();
        Assert.Contains("Row-1", cellText);
        Assert.Contains("Value-1", cellText);
    }

    [Fact]
    public void ReadTreeItems_PagesExpandedWpfTreeWithoutChangingExpansionOrSelection()
    {
        TreeViewItem? root = null;
        TreeViewItem? childA = null;
        TreeViewItem? childB = null;
        using var window = FixtureWindow.Create(() =>
        {
            root = new TreeViewItem { Header = "Tree-Root", IsExpanded = true };
            AutomationProperties.SetAutomationId(root, "TreeRoot");
            childA = new TreeViewItem { Header = "Tree-Child-A" };
            childB = new TreeViewItem { Header = "Tree-Child-B" };
            AutomationProperties.SetAutomationId(childA, "TreeChildA");
            AutomationProperties.SetAutomationId(childB, "TreeChildB");
            root.Items.Add(childA);
            root.Items.Add(childB);
            return new TreeView { Items = { root } };
        });

        var wasExpanded = window.Invoke(() => root!.IsExpanded);
        var wasSelected = window.Invoke(() => root!.IsSelected || childA!.IsSelected || childB!.IsSelected);
        var result = UiCli.Run(
            "read-tree-items", "--hwnd", window.HwndArgument, "--strategy", "AutomationId", "--value", "TreeRoot",
            "--offset", "1", "--limit", "1", "--maxNodes", "1000", "--maxDurationMs", "120000");

        Assert.True(result.Success, result.RawOutput);
        Assert.Equal(1, result.Json.GetProperty("returned").GetInt32());
        var item = result.Json.GetProperty("items")[0].GetProperty("element");
        Assert.Equal("Tree-Child-B", item.GetProperty("name").GetString());
        Assert.Equal("TreeChildB", item.GetProperty("automationId").GetString());
        Assert.True(window.Invoke(() => root!.IsExpanded) == wasExpanded);
        Assert.Equal(wasSelected, window.Invoke(() => root!.IsSelected || childA!.IsSelected || childB!.IsSelected));
    }

    [Fact]
    public void SelectItem_ReplacesMultipleListBoxSelectionThenVerifiesNoOp()
    {
        ListBox? list = null;
        ListBoxItem? target = null;
        ListBoxItem? other = null;
        using var window = FixtureWindow.Create(() =>
        {
            target = new ListBoxItem { Content = "List-Target" };
            other = new ListBoxItem { Content = "List-Other" };
            AutomationProperties.SetAutomationId(target, "ListTarget");
            AutomationProperties.SetAutomationId(other, "ListOther");
            list = new ListBox
            {
                SelectionMode = SelectionMode.Extended,
                Items = { target, other },
            };
            AutomationProperties.SetAutomationId(list, "SelectionList");
            return list;
        });

        window.Invoke(() =>
        {
            list!.SelectedItems.Add(target!);
            list.SelectedItems.Add(other!);
        });
        Assert.Equal(2, window.Invoke(() => list!.SelectedItems.Count));

        var replaced = UiCli.Run(
            "select-item", "--hwnd", window.HwndArgument, "--strategy", "AutomationId", "--value", "ListTarget", "--mode", "replace",
            "--maxNodes", "1000", "--maxDurationMs", "120000");
        Assert.True(replaced.Success, replaced.RawOutput);
        Assert.True(replaced.Json.GetProperty("verified").GetBoolean());
        Assert.True(replaced.Json.GetProperty("actionAttempted").GetBoolean());
        Assert.Equal(1, window.Invoke(() => list!.SelectedItems.Count));
        Assert.True(window.Invoke(() => target!.IsSelected));
        Assert.False(window.Invoke(() => other!.IsSelected));

        var noOp = UiCli.Run(
            "select-item", "--hwnd", window.HwndArgument, "--strategy", "AutomationId", "--value", "ListTarget", "--mode", "replace",
            "--maxNodes", "1000", "--maxDurationMs", "120000");
        Assert.True(noOp.Success, noOp.RawOutput);
        Assert.True(noOp.Json.GetProperty("verified").GetBoolean());
        Assert.False(noOp.Json.GetProperty("actionAttempted").GetBoolean());
        Assert.True(noOp.Json.GetProperty("noOp").GetBoolean());
        Assert.Equal(1, window.Invoke(() => list!.SelectedItems.Count));
    }

    [Fact]
    public void SelectRow_LogicalFullRowDataGridAndExposedListBoxVerifySelection()
    {
        DataGrid? grid = null;
        using var gridWindow = FixtureWindow.Create(() =>
        {
            grid = (DataGrid)CreateDataGrid("FullRowGrid", fullRowSelection: true);
            return grid;
        });

        var logical = UiCli.Run(
            "select-row", "--hwnd", gridWindow.HwndArgument, "--strategy", "AutomationId", "--value", "FullRowGrid",
            "--row", "1", "--rowKind", "logical", "--mode", "replace", "--maxNodes", "2000", "--maxDurationMs", "120000");
        Assert.True(logical.Success, logical.RawOutput);
        Assert.True(logical.Json.GetProperty("verified").GetBoolean());
        Assert.True(gridWindow.Invoke(() => grid!.SelectedIndex == 1));
        Assert.Equal(1, gridWindow.Invoke(() => grid!.SelectedItems.Count));

        ListBox? list = null;
        using var listWindow = FixtureWindow.Create(() =>
        {
            list = new ListBox
            {
                Items = { new ListBoxItem { Content = "Exposed-A" }, new ListBoxItem { Content = "Exposed-B" } },
            };
            AutomationProperties.SetAutomationId(list, "ExposedList");
            return list;
        });

        var exposed = UiCli.Run(
            "select-row", "--hwnd", listWindow.HwndArgument, "--strategy", "AutomationId", "--value", "ExposedList",
            "--row", "1", "--rowKind", "exposed", "--mode", "replace", "--maxNodes", "2000", "--maxDurationMs", "120000");
        Assert.True(exposed.Success, exposed.RawOutput);
        Assert.True(exposed.Json.GetProperty("verified").GetBoolean());
        Assert.True(listWindow.Invoke(() => list!.SelectedIndex == 1));
    }

    [Fact]
    public async Task McpReadRows_ForwardsARealWpfDataGridHappyPath()
    {
        using var window = FixtureWindow.Create(() => CreateDataGrid("McpRowsGrid", fullRowSelection: false));
        var tools = new UiAutomationTools(new CliRunner(NullLogger.Instance, AppContext.BaseDirectory, AppContext.BaseDirectory));

        var result = await tools.ReadRows(window.HwndArgument, 0, 1, "AutomationId", "McpRowsGrid", maxNodes: 1000, maxDurationMs: 120000);

        Assert.Null(result.WrapperErrorCode);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Result!.Value.GetProperty("success").GetBoolean());
        Assert.Equal(1, result.Result.Value.GetProperty("returned").GetInt32());
    }

    private static UIElement CreateDataGrid(string automationId, bool fullRowSelection)
    {
        var rows = new ObservableCollection<GridRow>
        {
            new("Row-0", "Value-0"),
            new("Row-1", "Value-1"),
            new("Row-2", "Value-2"),
        };
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.None,
            IsReadOnly = true,
            SelectionUnit = fullRowSelection ? DataGridSelectionUnit.FullRow : DataGridSelectionUnit.CellOrRowHeader,
            SelectionMode = DataGridSelectionMode.Extended,
            ItemsSource = rows,
        };
        AutomationProperties.SetAutomationId(grid, automationId);
        grid.Columns.Add(new DataGridTextColumn { Header = "Label", Binding = new System.Windows.Data.Binding(nameof(GridRow.Label)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Value", Binding = new System.Windows.Data.Binding(nameof(GridRow.Value)) });
        return grid;
    }
}
