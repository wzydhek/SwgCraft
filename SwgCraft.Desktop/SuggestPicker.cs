using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace SwgCraft.Desktop;

/// <summary>
/// An editable combo box that filters its list as you type: every word typed has to appear somewhere in the entry
/// (case-insensitive), so "scope adv" finds "Class: Advanced Weapon Scope". Pick with the mouse, the arrow keys + Enter,
/// or just type the whole name (or enough of it that only one entry is left) and press Enter/Tab.
/// </summary>
public sealed class SuggestComboBox : ComboBox
{
    IReadOnlyList<RefOption> _all;
    ListCollectionView _view;
    string[] _words = Array.Empty<string>();
    TextBox? _tb;
    bool _busy;
    string? _typedText;   // what the user had typed (and where the caret was) when we opened the list
    int _typedCaret;
    readonly DispatcherTimer _filterTimer;   // filtering waits until typing pauses briefly, instead of running on every key

    /// <summary>How long typing has to pause before the list is filtered.</summary>
    static readonly TimeSpan FilterDelay = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// A virtualizing list panel: only the rows actually on screen get built. The default ComboBox panel builds a row for every
    /// entry, which with ~1,700 schematics and looted items took up to a couple of seconds after each keystroke.
    /// </summary>
    static readonly ItemsPanelTemplate VirtualPanel = CreateVirtualPanel();

    static ItemsPanelTemplate CreateVirtualPanel()
    {
        var t = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        t.Seal();
        return t;
    }

    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(
        nameof(Key), typeof(string), typeof(SuggestComboBox),
        new FrameworkPropertyMetadata("", (d, e) => ((SuggestComboBox)d).OnKeyChanged((string?)e.NewValue ?? "")));

    /// <summary>The key of the chosen option ("" for none). Bind this, not SelectedValue.</summary>
    public string Key
    {
        get => (string)GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    /// <summary>When true, clearing the text and leaving the box means "nothing chosen" (Key becomes ""). Off for grid cells.</summary>
    public bool AllowEmpty { get; set; }

    public SuggestComboBox(IReadOnlyList<RefOption> options)
    {
        _all = options;
        _view = NewView(options);
        IsEditable = true;
        IsTextSearchEnabled = false;
        StaysOpenOnEdit = true;
        DisplayMemberPath = nameof(RefOption.Display);

        ItemsPanel = VirtualPanel;
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(this, true);   // scroll by row, which virtualization needs
        MaxDropDownHeight = 320;

        _filterTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = FilterDelay };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); ApplyFilter(); };

        ItemsSource = _view;
    }

    // our own view per box, so filtering never affects other pickers
    ListCollectionView NewView(IReadOnlyList<RefOption> options)
    {
        var view = new ListCollectionView(options.ToList());
        view.Filter = Match;
        return view;
    }

    /// <summary>Replaces the list of entries (for example after the categories were edited), keeping the current choice if it still exists.</summary>
    public void SetOptions(IReadOnlyList<RefOption> options)
    {
        _filterTimer.Stop();
        var key = Key;
        _all = options;
        _words = Array.Empty<string>();
        _view = NewView(options);
        ItemsSource = _view;
        OnKeyChanged(key);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (IsKeyboardFocusWithin) return;   // focus only moved between the box, its text field and its list
        FlushFilter();

        var resolved = ResolveKey();
        if (resolved != null && resolved != Key) { Key = resolved; return; }

        // Nothing usable was typed (or nothing changed): show the current choice again instead of leftover text.
        _busy = true;
        try
        {
            _words = Array.Empty<string>();
            _view.Refresh();
            var current = Key.Length == 0 ? null : _all.FirstOrDefault(o => o.Key == Key);
            SelectedItem = current;
            if (_tb != null) _tb.Text = current?.Display ?? "";
        }
        finally { _busy = false; }
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_tb != null) _tb.TextChanged -= OnTextChanged;
        _tb = GetTemplateChild("PART_EditableTextBox") as TextBox;
        if (_tb != null) _tb.TextChanged += OnTextChanged;
    }

    bool Match(object o)
    {
        var display = ((RefOption)o).Display;
        foreach (var w in _words)
            if (display.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) return false;
        return true;
    }

    /// <summary>Picking an entry (mouse, or arrows + Enter) writes the key straight away, so the choice never depends on when the grid commits the cell.</summary>
    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (_busy || SelectedItem is not RefOption picked || picked.Key == Key) return;
        _busy = true;
        try { Key = picked.Key; }
        finally { _busy = false; }
    }

    void OnKeyChanged(string key)
    {
        _busy = true;
        try { SelectedItem = key.Length == 0 ? null : _all.FirstOrDefault(o => o.Key == key); }
        finally { _busy = false; }
    }

    void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_busy || _tb == null) return;
        var text = _tb.Text ?? "";
        if (SelectedItem is RefOption sel && sel.Display == text) return;   // the text came from picking an entry, not from typing

        // restart the wait: the list is filtered once typing pauses
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    /// <summary>Runs a filter that is still waiting, so keys like Enter, Tab and the arrows act on what was actually typed.</summary>
    void FlushFilter()
    {
        if (!_filterTimer.IsEnabled) return;
        _filterTimer.Stop();
        ApplyFilter();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // (spelled out in full: inside this class, plain "Key" means the chosen-option property)
        if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Tab or System.Windows.Input.Key.Down
                  or System.Windows.Input.Key.Up or System.Windows.Input.Key.PageDown or System.Windows.Input.Key.PageUp)
            FlushFilter();
        base.OnPreviewKeyDown(e);
    }

    void ApplyFilter()
    {
        if (_tb == null) return;
        var text = _tb.Text ?? "";
        if (SelectedItem is RefOption sel && sel.Display == text) return;

        _busy = true;
        try
        {
            int caret = _tb.CaretIndex;
            _words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _view.Refresh();                       // may clear the selection, which can wipe the text, so put it back
            _tb.Text = text;
            _tb.CaretIndex = Math.Min(caret, text.Length);
        }
        finally { _busy = false; }

        _typedText = text;
        _typedCaret = Math.Min(_tb.CaretIndex, text.Length);
        IsDropDownOpen = _view.Count > 0;
        KeepCaret();
    }

    protected override void OnDropDownOpened(EventArgs e)
    {
        base.OnDropDownOpened(e);
        KeepCaret();
    }

    protected override void OnDropDownClosed(EventArgs e)
    {
        base.OnDropDownClosed(e);
        _typedText = null;
    }

    /// <summary>
    /// Opening the list of an editable ComboBox selects all of its text, so the next keystroke would replace what was just typed.
    /// Put the caret back at the end of the typed text (right away, and again once the box has finished opening).
    /// </summary>
    void KeepCaret()
    {
        if (_tb == null || _typedText == null) return;
        var text = _typedText;
        int caret = _typedCaret;
        void Restore()
        {
            // only if the user hasn't typed again in the meantime
            if (_tb != null && _tb.Text == text && _tb.SelectionLength > 0) _tb.Select(caret, 0);
        }
        Restore();
        Dispatcher.BeginInvoke(new Action(Restore), DispatcherPriority.Input);
    }

    /// <summary>The key the user ended up with, or null if the text matches nothing (the caller then keeps the old value).</summary>
    public string? ResolveKey()
    {
        FlushFilter();
        var text = (_tb?.Text ?? Text ?? "").Trim();
        if (SelectedItem is RefOption sel && sel.Display == text) return sel.Key;
        if (text.Length == 0) return AllowEmpty ? "" : null;
        var exact = _all.FirstOrDefault(o => string.Equals(o.Display, text, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact.Key;
        if (_view.Count == 1) return ((RefOption)_view.GetItemAt(0)).Key;   // only one entry left after filtering
        return null;
    }

    public void FocusForEditing()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyTemplate();
            _tb?.Focus();
            _tb?.SelectAll();
            IsDropDownOpen = true;
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