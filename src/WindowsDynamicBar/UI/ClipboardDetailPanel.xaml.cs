using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsDynamicBar.Core.Clipboard;

namespace WindowsDynamicBar.UI;

/// <summary>
/// Clipboard history card: search box, newest-first rows (type icon, 60-char preview, age; pin
/// and delete appear on hover), "pause" and "clear all" on the title edge. Enter / click puts the
/// item back on the clipboard; Up/Down move, Delete removes, Esc closes. Content is only shown,
/// never logged.
/// </summary>
public partial class ClipboardDetailPanel : UserControl
{
    private const string Mono = "Cascadia Mono, Consolas, Courier New";
    private const string Icons = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private const double RowHeight = 28;

    private static readonly Brush NameBrush = Frozen(Color.FromRgb(0xE6, 0xE0, 0xA8));
    private static readonly Brush MetricBrush = Frozen(Color.FromRgb(0x8A, 0xB4, 0x78));
    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0x9A, 0xA0, 0x88));
    private static readonly Brush ValueBrush = Frozen(Color.FromRgb(0xE8, 0xE8, 0xDC));
    private static readonly Brush DimValueBrush = Frozen(Color.FromRgb(0x6F, 0x74, 0x66));
    private static readonly Brush SelectedBrush = Frozen(Color.FromArgb(0x33, 0x8A, 0xB4, 0x78));
    private static readonly Brush HoverBrush = Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    private static readonly Brush PinBrush = Frozen(Color.FromRgb(0xE8, 0xB0, 0x4B));

    private sealed class Row
    {
        public required ClipItem Item;
        public required Border Root;
        public required TextBlock Age;
        public required FrameworkElement Actions;
        public required TextBlock Star;
    }

    private readonly List<Row> _rows = new();
    private IReadOnlyList<ClipItem> _all = Array.Empty<ClipItem>();
    private int _selected = -1;
    private int _limit = 10;
    private bool _paused;

    public ClipboardDetailPanel()
    {
        InitializeComponent();
        TitleText.Foreground = NameBrush;
    }

    public event Action<ClipItem>? ItemChosen;
    public event Action<long, bool>? PinRequested;
    public event Action<long>? DeleteRequested;
    public event Action? ClearRequested;
    public event Action<bool>? PauseRequested;
    public event Action? CloseRequested;

    public string Query => SearchBox.Text;

    /// <summary>Put the caret in the search box (only when opened on purpose, e.g. by the hotkey).</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

#if DEBUG
    internal void DebugSetQuery(string q) => SearchBox.Text = q;
#endif

    public void ResetQuery()
    {
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        _selected = -1;
    }

    public void Render(IReadOnlyList<ClipItem> items, int limit, bool paused, bool enabled)
    {
        _all = items;
        _limit = limit;
        _paused = paused;
        PauseText.Text = paused ? "resume" : "pause";
        ClearButton.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildRows(enabled);
        SetTitle(enabled);
    }

    /// <summary>Refresh only the age column (called every few seconds while visible).</summary>
    public void RefreshAges()
    {
        var now = DateTime.UtcNow;
        foreach (var r in _rows) r.Age.Text = ClipboardHistory.Age(now - r.Item.CreatedUtc);
    }

    private void SetTitle(bool enabled)
    {
        int unpinned = 0;
        foreach (var i in _all) if (!i.Pinned) unpinned++;
        TitleText.Inlines.Clear();
        TitleText.Inlines.Add(new Run("┤") { Foreground = NameBrush });
        TitleText.Inlines.Add(new Run("clip") { Foreground = MetricBrush });
        TitleText.Inlines.Add(new Run("├") { Foreground = NameBrush });
        if (enabled)
            TitleText.Inlines.Add(new Run($"┤{unpinned}/{_limit}├") { Foreground = NameBrush });
        if (_paused)
            TitleText.Inlines.Add(new Run("┤paused├") { Foreground = PinBrush });
    }

    private void RebuildRows(bool enabled)
    {
        foreach (var r in _rows) Unhook(r);
        _rows.Clear();
        Rows.Children.Clear();

        var shown = ClipboardHistory.Filter(_all, SearchBox.Text);
        var now = DateTime.UtcNow;
        foreach (var item in shown)
        {
            var row = BuildRow(item, now);
            _rows.Add(row);
            Rows.Children.Add(row.Root);
        }

        if (!enabled) EmptyText.Text = "Clipboard history is off (Settings > Features).";
        else if (_all.Count == 0) EmptyText.Text = _paused ? "Paused. Copies are not recorded." : "Copy something and it shows up here.";
        else if (shown.Count == 0) EmptyText.Text = "No match.";
        EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _selected = shown.Count == 0 ? -1 : Math.Clamp(_selected < 0 ? 0 : _selected, 0, shown.Count - 1);
        UpdateSelection();
    }

    private Row BuildRow(ClipItem item, DateTime now)
    {
        var grid = new Grid { Height = RowHeight };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = item.Kind switch { ClipKind.Link => "", ClipKind.Image => "", ClipKind.Files => "", _ => "" },
            FontFamily = new FontFamily(Icons), FontSize = 12, Foreground = item.Pinned ? PinBrush : LabelBrush,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
        };
        grid.Children.Add(icon);

        var preview = new TextBlock
        {
            Text = item.Preview, FontFamily = new FontFamily(Mono), FontSize = 10.5,
            Foreground = item.CanRestore ? ValueBrush : DimValueBrush,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(preview, 1);
        grid.Children.Add(preview);

        var age = new TextBlock
        {
            Text = ClipboardHistory.Age(now - item.CreatedUtc), FontFamily = new FontFamily(Mono), FontSize = 10,
            Foreground = LabelBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(age, 2);
        grid.Children.Add(age);

        var star = new TextBlock
        {
            Text = item.Pinned ? "" : "", FontFamily = new FontFamily(Icons), FontSize = 11,
            Foreground = item.Pinned ? PinBrush : LabelBrush, Margin = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = item.Pinned ? "Unpin" : "Pin",
            Background = Brushes.Transparent, Tag = item,
        };
        var del = new TextBlock
        {
            Text = "", FontFamily = new FontFamily(Icons), FontSize = 10, Foreground = LabelBrush,
            Margin = new Thickness(4, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
            ToolTip = "Delete", Background = Brushes.Transparent, Tag = item,
        };
        star.MouseLeftButtonUp += OnStarUp;
        del.MouseLeftButtonUp += OnDeleteUp;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Visibility = item.Pinned ? Visibility.Visible : Visibility.Hidden };
        actions.Children.Add(star);
        actions.Children.Add(del);
        Grid.SetColumn(actions, 3);
        grid.Children.Add(actions);

        var root = new Border
        {
            Child = grid, CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 3, 0),
            Background = Brushes.Transparent, Cursor = item.CanRestore ? Cursors.Hand : Cursors.Arrow, Tag = item,
            ToolTip = item.CanRestore ? null : "The full image is no longer kept (only the newest 3 images are)",
        };
        if (item.Kind == ClipKind.Image && item.ThumbPng is { } thumb)
            root.ToolTip = new Image { Source = DecodeThumb(thumb, 96), Width = 96 };
        root.MouseEnter += OnRowEnter;
        root.MouseLeave += OnRowLeave;
        root.MouseLeftButtonUp += OnRowUp;
        return new Row { Item = item, Root = root, Age = age, Actions = actions, Star = star };
    }

    private void Unhook(Row r)
    {
        r.Root.MouseEnter -= OnRowEnter;
        r.Root.MouseLeave -= OnRowLeave;
        r.Root.MouseLeftButtonUp -= OnRowUp;
        r.Star.MouseLeftButtonUp -= OnStarUp;
        if (r.Actions is Panel p && p.Children.Count > 1 && p.Children[1] is TextBlock del) del.MouseLeftButtonUp -= OnDeleteUp;
    }

    /// <summary>Thumbnail decoded at the size it is shown (frozen).</summary>
    private static BitmapImage DecodeThumb(byte[] png, int width)
    {
        using var ms = new MemoryStream(png);
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.DecodePixelWidth = width;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    // ------------------------------------------------------------------ input

    private Row? RowOf(object sender) => sender is FrameworkElement { Tag: ClipItem it } ? _rows.Find(r => r.Item.Id == it.Id) : null;

    private void OnRowEnter(object sender, MouseEventArgs e)
    {
        if (RowOf(sender) is not { } r) return;
        r.Actions.Visibility = Visibility.Visible;
        if (_rows.IndexOf(r) != _selected) r.Root.Background = HoverBrush;
    }

    private void OnRowLeave(object sender, MouseEventArgs e)
    {
        if (RowOf(sender) is not { } r) return;
        r.Actions.Visibility = r.Item.Pinned ? Visibility.Visible : Visibility.Hidden;
        UpdateSelection();
    }

    private void OnRowUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (RowOf(sender) is { } r && r.Item.CanRestore) ItemChosen?.Invoke(r.Item);
    }

    private void OnStarUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: ClipItem it }) PinRequested?.Invoke(it.Id, !it.Pinned);
    }

    private void OnDeleteUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: ClipItem it }) DeleteRequested?.Invoke(it.Id);
    }

    private void OnPauseClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        PauseRequested?.Invoke(!_paused);
    }

    private void OnClearClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ClearRequested?.Invoke();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _selected = 0;
        RebuildRows(enabled: true);
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: Move(+1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.Enter:
                if (_selected >= 0 && _selected < _rows.Count && _rows[_selected].Item.CanRestore)
                    ItemChosen?.Invoke(_rows[_selected].Item);
                e.Handled = true;
                break;
            case Key.Delete when SearchBox.Text.Length == 0 || SearchBox.CaretIndex == SearchBox.Text.Length:
                if (_selected >= 0 && _selected < _rows.Count) { DeleteRequested?.Invoke(_rows[_selected].Item.Id); e.Handled = true; }
                break;
            case Key.Escape:
                CloseRequested?.Invoke();
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        if (_rows.Count == 0) return;
        _selected = Math.Clamp(_selected + delta, 0, _rows.Count - 1);
        UpdateSelection();
        var row = _rows[_selected].Root;
        row.BringIntoView();
    }

    private void UpdateSelection()
    {
        for (int i = 0; i < _rows.Count; i++)
            _rows[i].Root.Background = i == _selected ? SelectedBrush : Brushes.Transparent;
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
