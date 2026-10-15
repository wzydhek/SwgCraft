using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SwgCraft.Desktop;

/// <summary>
/// A text box with an auto-suggest list, behaving like a Windows Forms combo box with AutoComplete "Suggest":
/// <list type="bullet">
/// <item>Typing filters the list: every word typed has to appear somewhere in the entry (case-insensitive), so
///       "scope adv" finds "Class: Advanced Weapon Scope". The best match (an exact name, else the first) is highlighted.</item>
/// <item>Up/Down/PageUp/PageDown move the highlight; Enter or Tab takes the highlighted entry; Escape closes the list.</item>
/// <item>Clicking an entry takes it straight away. Just hovering over the list never changes anything.</item>
/// <item>Leaving the box keeps an exact or only match of what was typed; otherwise the previous choice is shown again.</item>
/// <item>The arrow button (or Alt+Down / F4) shows the whole list.</item>
/// </list>
/// The chosen entry is <see cref="Key"/>; nothing is chosen while typing, so a half-typed name can't change the value.
/// </summary>
public sealed class SuggestComboBox : UserControl
{
    IReadOnlyList<RefOption> _all;
    ListCollectionView _view;
    string[] _words = Array.Empty<string>();
    readonly TextBox _text;
    readonly Popup _popup;
    readonly ListBox _list;
    bool _suppressText;   // the text is being set by code, not typed

    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(
        nameof(Key), typeof(string), typeof(SuggestComboBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                                      (d, e) => ((SuggestComboBox)d).ShowKey()));

    /// <summary>The key of the chosen option ("" for none). Bind this.</summary>
    public string Key
    {
        get => (string)GetValue(KeyProperty);
        set => SetValue(KeyProperty, value ?? "");
    }

    /// <summary>When true, clearing the text and leaving the box means "nothing chosen" (Key becomes ""). Off for grid cells.</summary>
    public bool AllowEmpty { get; set; }

    public SuggestComboBox(IReadOnlyList<RefOption> options)
    {
        _all = options;
        _view = NewView(options);
        IsTabStop = false;   // the text box inside takes the focus
        Focusable = false;

        _text = new TextBox { VerticalContentAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(0) };
        _text.TextChanged += OnTextChanged;
        _text.PreviewKeyDown += OnTextKeyDown;
        _text.GotKeyboardFocus += (_, _) => _text.SelectAll();
        _text.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_text.IsKeyboardFocusWithin) return;   // first click into the box selects everything, later clicks place the cursor
            e.Handled = true;
            _text.Focus();
        };

        var arrow = new Button
        {
            Content = new TextBlock { Text = "▾", FontSize = 11, Margin = new Thickness(0, -1, 0, 0) },
            Width = 18,
            Focusable = false,
            IsTabStop = false,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Background = Brushes.Transparent,
            ToolTip = "Show the whole list",
        };
        arrow.Click += (_, _) =>
        {
            if (_popup!.IsOpen) { _popup.IsOpen = false; return; }
            _text.Focus();
            OpenFullList();
        };

        _list = new ListBox
        {
            Focusable = false,
            DisplayMemberPath = nameof(RefOption.Display),
            BorderThickness = new Thickness(0),
            ItemsSource = _view,
        };
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        // the highlight looks the same whether or not the list itself has focus (it never does)
        _list.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = SystemColors.HighlightBrush;
        _list.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = SystemColors.HighlightTextBrush;
        _list.PreviewMouseLeftButtonDown += OnListMouseDown;

        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            StaysOpen = true,
            AllowsTransparency = false,
            Child = new Border
            {
                BorderBrush = SystemColors.ActiveBorderBrush,
                BorderThickness = new Thickness(1),
                Background = SystemColors.WindowBrush,
                MaxHeight = 320,
                Child = _list,
            },
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(arrow, 1);
        grid.Children.Add(_text);
        grid.Children.Add(arrow);
        grid.Children.Add(_popup);

        Content = new Border
        {
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            Background = SystemColors.WindowBrush,
            Child = grid,
        };
        MinHeight = 22;
    }

    // our own view per box, so filtering never affects other pickers
    ListCollectionView NewView(IReadOnlyList<RefOption> options)
    {
        var view = new ListCollectionView(options.ToList());
        view.Filter = o =>
        {
            var display = ((RefOption)o).Display;
            foreach (var w in _words)
                if (display.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        };
        return view;
    }

    RefOption? Current => Key.Length == 0 ? null : _all.FirstOrDefault(o => o.Key == Key);

    /// <summary>Replaces the list of entries (for example after the categories were edited), keeping the current choice.</summary>
    public void SetOptions(IReadOnlyList<RefOption> options)
    {
        _all = options;
        _words = Array.Empty<string>();
        _view = NewView(options);
        _list.ItemsSource = _view;
        if (!_text.IsKeyboardFocusWithin) ShowKey();
    }

    /// <summary>Shows the chosen entry's name in the box (or the key itself if it isn't in the list, so a dangling value is visible).</summary>
    void ShowKey()
    {
        var current = Current;
        SetText(current?.Display ?? Key);
    }

    void SetText(string text)
    {
        _suppressText = true;
        try
        {
            _text.Text = text;
            _text.CaretIndex = text.Length;
        }
        finally { _suppressText = false; }
    }

    // ------------------------------------------------------------ typing

    void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressText) return;
        Filter(_text.Text ?? "");
        if (_view.Count == 0) { _popup.IsOpen = false; return; }
        HighlightBest(_text.Text ?? "");
        Open();
    }

    void Filter(string text)
    {
        _words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _view.Refresh();
    }

    /// <summary>Highlights the entry named exactly as typed, else the first one left.</summary>
    void HighlightBest(string text)
    {
        var t = text.Trim();
        RefOption? exact = null;
        if (t.Length > 0)
            foreach (RefOption o in _view)
                if (string.Equals(o.Display, t, StringComparison.OrdinalIgnoreCase)) { exact = o; break; }
        Highlight(exact ?? (_view.Count > 0 ? (RefOption)_view.GetItemAt(0) : null));
    }

    void Highlight(RefOption? option)
    {
        _list.SelectedItem = option;
        if (option != null) _list.ScrollIntoView(option);
    }

    void Open()
    {
        if (_popup.IsOpen) return;
        _popup.MinWidth = Math.Max(ActualWidth, 200);
        _popup.IsOpen = true;
    }

    /// <summary>The arrow button / Alt+Down: every entry, with the current choice highlighted.</summary>
    void OpenFullList()
    {
        Filter("");
        Highlight(Current);
        Open();
    }

    void OnTextKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;   // (in this class plain "Key" is the chosen-option property)
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        switch (key)
        {
            case System.Windows.Input.Key.Down when alt:
            case System.Windows.Input.Key.F4:
                if (_popup.IsOpen) _popup.IsOpen = false; else OpenFullList();
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Down:
            case System.Windows.Input.Key.Up:
            case System.Windows.Input.Key.PageDown:
            case System.Windows.Input.Key.PageUp:
                if (!_popup.IsOpen)
                {
                    if (key != System.Windows.Input.Key.Down) return;   // let a grid use the other keys when the list is closed
                    Filter(_text.Text == (Current?.Display ?? "") ? "" : _text.Text ?? "");
                    HighlightBest(_text.Text ?? "");
                    Open();
                }
                else MoveHighlight(key switch
                {
                    System.Windows.Input.Key.Down => 1,
                    System.Windows.Input.Key.Up => -1,
                    System.Windows.Input.Key.PageDown => 10,
                    _ => -10,
                });
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Enter:
            case System.Windows.Input.Key.Tab:
                if (_popup.IsOpen && _list.SelectedItem is RefOption pick) Commit(pick);
                else ResolveAndShow();
                break;   // not handled: Enter still finishes a grid cell and Tab still moves on
            case System.Windows.Input.Key.Escape:
                if (_popup.IsOpen)
                {
                    _popup.IsOpen = false;
                    e.Handled = true;
                }
                else ShowKey();   // a grid then cancels the edit as usual
                break;
        }
    }

    void MoveHighlight(int delta)
    {
        if (_view.Count == 0) return;
        int i = _list.SelectedItem is RefOption cur ? _view.IndexOf(cur) : -1;
        int next = i < 0 ? (delta > 0 ? 0 : _view.Count - 1) : Math.Clamp(i + delta, 0, _view.Count - 1);
        Highlight((RefOption)_view.GetItemAt(next));
    }

    // ------------------------------------------------------------ choosing

    /// <summary>A click on an entry takes it at once; handled here so the list never takes the focus away from the text box.</summary>
    void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(_list, e.OriginalSource as DependencyObject) is not ListBoxItem item) return;   // e.g. the scroll bar
        if (_list.ItemContainerGenerator.ItemFromContainer(item) is RefOption option)
        {
            Commit(option);
            e.Handled = true;
        }
    }

    void Commit(RefOption option)
    {
        _popup.IsOpen = false;
        Key = option.Key;
        SetText(option.Display);   // also when the key didn't change (the text may have been edited)
        Filter("");
    }

    /// <summary>
    /// The key the typed text stands for: the current choice if its name is still shown, an entry named exactly like the text,
    /// the only entry left after filtering, or "" for an empty box when <see cref="AllowEmpty"/>. Null when it stands for nothing
    /// (the caller then keeps the old value).
    /// </summary>
    public string? ResolveKey()
    {
        var text = (_text.Text ?? "").Trim();
        if (Current is { } cur && string.Equals(cur.Display, text, StringComparison.Ordinal)) return cur.Key;
        if (text.Length == 0) return AllowEmpty ? "" : null;
        var exact = _all.FirstOrDefault(o => string.Equals(o.Display, text, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact.Key;
        Filter(text);
        return _view.Count == 1 ? ((RefOption)_view.GetItemAt(0)).Key : null;
    }

    /// <summary>Takes what the text stands for, or shows the previous choice again.</summary>
    void ResolveAndShow()
    {
        var resolved = ResolveKey();
        if (resolved != null) Key = resolved;
        ShowKey();
        Filter("");
    }

    protected override void OnIsKeyboardFocusWithinChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusWithinChanged(e);
        if ((bool)e.NewValue) return;
        _popup.IsOpen = false;
        ResolveAndShow();
    }

    /// <summary>For grid cells: focus the box with its text selected and show the whole list.</summary>
    public void FocusForEditing()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _text.Focus();
            _text.SelectAll();
            OpenFullList();
        }), DispatcherPriority.Input);
    }
}

/// <summary>A grid column showing the display text of a stored key and editing it with a <see cref="SuggestComboBox"/>.</summary>
public sealed class PickerColumn : DataGridColumn
{
    sealed class KeyToDisplay : IValueConverter
    {
        readonly PickerColumn _owner;
        public KeyToDisplay(PickerColumn owner) => _owner = owner;

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var key = value as string ?? "";
            if (key.Length == 0) return "(choose...)";
            return _owner._display.TryGetValue(key, out var d) ? d : key;   // an unknown key is shown as it is, so a dangling reference is visible
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    readonly string _path;
    readonly KeyToDisplay _converter;
    Dictionary<string, string> _display = new(StringComparer.Ordinal);
    IReadOnlyList<RefOption> _options = Array.Empty<RefOption>();

    /// <param name="path">The view-model property holding the stored key.</param>
    public PickerColumn(string header, string path)
    {
        Header = header;
        _path = path;
        _converter = new KeyToDisplay(this);
        Width = new DataGridLength(1, DataGridLengthUnitType.Star);
    }

    public void SetOptions(IReadOnlyList<RefOption> options)
    {
        _options = options;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var o in options) map.TryAdd(o.Key, o.Display);
        _display = map;
    }

    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem)
    {
        var tb = new TextBlock { Margin = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
        tb.SetBinding(TextBlock.TextProperty, new Binding(_path) { Converter = _converter, Mode = BindingMode.OneWay });
        return tb;
    }

    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem)
    {
        var cb = new SuggestComboBox(_options);
        cb.SetBinding(SuggestComboBox.KeyProperty, new Binding(_path) { Mode = BindingMode.TwoWay });
        return cb;
    }

    protected override object PrepareCellForEdit(FrameworkElement editingElement, RoutedEventArgs editingEventArgs)
    {
        if (editingElement is SuggestComboBox cb)
        {
            cb.FocusForEditing();
            return cb.Key;
        }
        return "";
    }

    protected override bool CommitCellEdit(FrameworkElement editingElement)
    {
        if (editingElement is SuggestComboBox cb)
        {
            var key = cb.ResolveKey();
            if (key != null) cb.Key = key;   // two-way binding: this writes it to the row
        }
        return true;
    }
}