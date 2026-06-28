using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SMSO.Net;

namespace SMSO.Launcher.Controls;

public partial class FastSelector : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(FastSelector),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.Register(nameof(SelectedItem), typeof(object), typeof(FastSelector),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedItemChanged));

    public static readonly DependencyProperty SelectedIndexProperty =
        DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(FastSelector),
            new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedIndexChanged));

    public static readonly DependencyProperty DisplayMemberPathProperty =
        DependencyProperty.Register(nameof(DisplayMemberPath), typeof(string), typeof(FastSelector),
            new PropertyMetadata(string.Empty, (d, _) => ((FastSelector)d).ApplyDisplayMemberPath()));

    public static readonly DependencyProperty EnableGroupHeadersProperty =
        DependencyProperty.Register(nameof(EnableGroupHeaders), typeof(bool), typeof(FastSelector),
            new PropertyMetadata(false, (d, _) => ((FastSelector)d).ApplyGroupHeaderTemplate()));

    public static readonly DependencyProperty ItemTemplateProperty =
        DependencyProperty.Register(nameof(ItemTemplate), typeof(DataTemplate), typeof(FastSelector),
            new PropertyMetadata(null, (d, _) => ((FastSelector)d).ApplyItemTemplate()));

    private bool _syncing;
    private bool _syncingFromIndex;
    private int _openGeneration;

    public FastSelector()
    {
        InitializeComponent();
        ApplyItemTemplate();
        PreviewMouseDown += OnPreviewMouseDown;
        IsEnabledChanged += (_, _) =>
        {
            Cursor = IsEnabled ? Cursors.Hand : Cursors.Arrow;
            if (!IsEnabled && DropPopup.IsOpen)
                DropPopup.IsOpen = false;
        };
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public string DisplayMemberPath
    {
        get => (string)GetValue(DisplayMemberPathProperty);
        set => SetValue(DisplayMemberPathProperty, value);
    }

    public bool EnableGroupHeaders
    {
        get => (bool)GetValue(EnableGroupHeadersProperty);
        set => SetValue(EnableGroupHeadersProperty, value);
    }

    public DataTemplate? ItemTemplate
    {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public event SelectionChangedEventHandler? SelectionChanged;

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (FastSelector)d;
        control.ItemsList.ItemsSource = e.NewValue as IEnumerable;
        control.ApplyGroupHeaderTemplate();
        if (control.SelectedIndex < 0 && control.GetItemCount() > 0)
            control.SelectedIndex = 0;
        control.RefreshDisplay();
    }

    private static void OnSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (FastSelector)d;
        if (control._syncing) return;

        control._syncing = true;
        try
        {
            control.ItemsList.SelectedItem = e.NewValue;
            control.SelectedIndex = control.IndexOfItem(e.NewValue);
            control.RefreshDisplay();
            if (!control._syncingFromIndex)
                control.RaiseSelectionChanged();
        }
        finally
        {
            control._syncing = false;
        }
    }

    private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (FastSelector)d;
        if (control._syncing) return;

        var index = (int)e.NewValue;
        if (index < 0 || index >= control.GetItemCount())
            return;

        control._syncingFromIndex = true;
        control._syncing = true;
        try
        {
            var item = control.GetItemAt(index);
            control.SetValue(SelectedItemProperty, item);
            control.ItemsList.SelectedIndex = index;
            control.RefreshDisplay();
            control.RaiseSelectionChanged();
        }
        finally
        {
            control._syncing = false;
            control._syncingFromIndex = false;
        }
    }

    private void ApplyItemTemplate()
    {
        if (ItemTemplate != null)
            ItemsList.ItemTemplate = ItemTemplate;

        ApplyDisplayMemberPath();
        ApplyGroupHeaderTemplate();
        RefreshDisplay();
    }

    private void ApplyDisplayMemberPath()
    {
        if (EnableGroupHeaders)
            return;

        ItemsList.DisplayMemberPath = string.IsNullOrWhiteSpace(DisplayMemberPath)
            ? string.Empty
            : DisplayMemberPath;
        RefreshDisplay();
    }

    private void ApplyGroupHeaderTemplate()
    {
        if (!EnableGroupHeaders)
        {
            ItemsList.ItemTemplate = ItemTemplate;
            ApplyDisplayMemberPath();
            return;
        }

        ItemsList.DisplayMemberPath = string.Empty;
        ItemsList.ItemTemplate = (DataTemplate)FindResource("WarpCourseItemTemplate");
    }

    private void RefreshDisplay()
    {
        var useText = !string.IsNullOrWhiteSpace(DisplayMemberPath) || EnableGroupHeaders;
        SelectionDisplay.Visibility = useText ? Visibility.Collapsed : Visibility.Visible;
        SelectionText.Visibility = useText ? Visibility.Visible : Visibility.Collapsed;

        if (!useText)
            return;

        if (SelectedItem == null)
        {
            SelectionText.Text = string.Empty;
            return;
        }

        if (SelectedItem is IWarpListEntry entry)
        {
            SelectionText.Text = entry.DisplayName;
            return;
        }

        if (string.IsNullOrWhiteSpace(DisplayMemberPath))
        {
            SelectionText.Text = SelectedItem.ToString() ?? string.Empty;
            return;
        }

        var prop = SelectedItem.GetType().GetProperty(DisplayMemberPath, BindingFlags.Public | BindingFlags.Instance);
        SelectionText.Text = prop?.GetValue(SelectedItem)?.ToString() ?? SelectedItem.ToString() ?? string.Empty;
    }

    private IEnumerable<object> EnumerateItems()
    {
        if (ItemsSource is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
                yield return item!;
        }
    }

    private int GetItemCount()
    {
        if (ItemsSource is IList list)
            return list.Count;
        return ItemsSource?.Cast<object>().Count() ?? 0;
    }

    private object? GetItemAt(int index)
    {
        if (index < 0)
            return null;

        var i = 0;
        foreach (var item in EnumerateItems())
        {
            if (i == index)
                return item;
            i++;
        }

        return null;
    }

    private int IndexOfItem(object? item)
    {
        if (item == null)
            return -1;

        var i = 0;
        foreach (var candidate in EnumerateItems())
        {
            if (Equals(candidate, item))
                return i;
            i++;
        }

        return -1;
    }

    private void MoveSelection(int direction)
    {
        var count = GetItemCount();
        if (count == 0)
            return;

        var index = SelectedIndex;
        if (index < 0)
            index = 0;

        index = Math.Clamp(index + direction, 0, count - 1);
        SelectedIndex = index;
    }

    private void RaiseSelectionChanged()
    {
        var added = SelectedItem == null ? Array.Empty<object>() : new[] { SelectedItem };
        var args = new SelectionChangedEventArgs(Selector.SelectionChangedEvent, Array.Empty<object>(), added);
        SelectionChanged?.Invoke(this, args);
    }

    private void MainBorder_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DropPopup.IsOpen)
            return;

        MoveSelection(e.Delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    private void MainBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (DropPopup.IsOpen)
        {
            _openGeneration++;
            DropPopup.IsOpen = false;
            return;
        }

        var generation = ++_openGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (generation != _openGeneration || DropPopup.IsOpen)
                return;
            OpenDropDown();
        });
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!DropPopup.IsOpen)
            return;

        if (IsDescendantOf(e.OriginalSource as DependencyObject, MainBorder) ||
            IsDescendantOf(e.OriginalSource as DependencyObject, DropPopup.Child))
            return;

        _openGeneration++;
        DropPopup.IsOpen = false;
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject? ancestor)
    {
        while (node != null)
        {
            if (node == ancestor)
                return true;
            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    private void OpenDropDown()
    {
        if (DropPopup.IsOpen)
            return;

        DropPopup.IsOpen = true;

        _syncing = true;
        try
        {
            ItemsList.SelectedItem = SelectedItem;
            if (SelectedItem != null)
                ItemsList.ScrollIntoView(SelectedItem);
            ItemsList.Focus();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || e.AddedItems.Count == 0)
            return;

        var newItem = e.AddedItems[0];
        if (Equals(SelectedItem, newItem))
            return;

        _syncing = true;
        try
        {
            SelectedItem = newItem;
            RefreshDisplay();
            RaiseSelectionChanged();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ItemsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsList.SelectedItem == null)
            return;

        DropPopup.IsOpen = false;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!DropPopup.IsOpen)
        {
            MoveSelection(e.Delta > 0 ? -1 : 1);
            e.Handled = true;
            return;
        }

        base.OnMouseWheel(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            MoveSelection(1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up)
        {
            MoveSelection(-1);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Enter or Key.Space)
        {
            if (DropPopup.IsOpen)
                DropPopup.IsOpen = false;
            else
                OpenDropDown();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && DropPopup.IsOpen)
        {
            DropPopup.IsOpen = false;
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }
}
