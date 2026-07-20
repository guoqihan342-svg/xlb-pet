using System;
using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LubanDesktopPet;

public partial class TodoWindow : Window
{
    private bool _settingPetSizeScale;
    private bool _petSizeAdjustmentActive;
    private bool _petSizeScaleNotificationQueued;
    private double _pendingPetSizeScale = 1;
    private int _displayedPetSizePercent = int.MinValue;
    private readonly Action _resetImeCompositionAfterFocusLossAction;
    private readonly Action _focusInputAction;
    private readonly Action _retryClipboardCopyAction;
    private string? _pendingClipboardCopyText;
    private bool _clipboardCopyRetryQueued;
    private bool _tailOnRight = true;
    private bool _allowClose;
    private bool _hasClosed;

    public TodoWindow()
    {
        InitializeComponent();
        _resetImeCompositionAfterFocusLossAction =
            ResetImeCompositionAfterFocusLoss;
        _focusInputAction = FocusInputCore;
        _retryClipboardCopyAction = RetryClipboardCopy;

        TextCompositionManager.AddPreviewTextInputStartHandler(
            TodoInput,
            TodoInput_PreviewTextInputStart);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(
            TodoInput,
            TodoInput_PreviewTextInputUpdate);
        TodoInput.PreviewTextInput += TodoInput_PreviewTextInputCommitted;
        TodoInput.LostKeyboardFocus += TodoInput_LostKeyboardFocus;
        PetSizeSlider.PreviewMouseLeftButtonDown += PetSizeSlider_PreviewMouseLeftButtonDown;
        PetSizeSlider.PreviewMouseLeftButtonUp += PetSizeSlider_PreviewMouseLeftButtonUp;
        PetSizeSlider.LostMouseCapture += PetSizeSlider_LostMouseCapture;
        PetSizeSlider.PreviewKeyDown += PetSizeSlider_PreviewKeyDown;
        PetSizeSlider.PreviewKeyUp += PetSizeSlider_PreviewKeyUp;
        PetSizeSlider.LostKeyboardFocus += PetSizeSlider_LostKeyboardFocus;
        PreviewKeyDown += TodoWindow_PreviewKeyDown;
        Closing += TodoWindow_Closing;
        Closed += TodoWindow_Closed;
    }

    public IEnumerable? Todos
    {
        get => TodoItemsControl.ItemsSource;
        set => TodoItemsControl.ItemsSource = value;
    }

    public bool IsImeComposing { get; private set; }

    public event Action<string>? AddRequested;

    public event Action<TodoItem>? TodoChanged;

    public event Action<TodoItem>? DeleteRequested;

    public event Action<double>? PetSizeScaleChanged;

    public event Action? PetSizeAdjustmentStarted;

    public event Action? PetSizeAdjustmentCompleted;

    public event EventHandler? CloseRequested;

    public event EventHandler? ExitRequested;

    public event Action<bool>? ImeCompositionChanged;

    public void FocusInput()
    {
        if (!IsVisible)
        {
            return;
        }

        Activate();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, _focusInputAction);
    }

    private void FocusInputCore()
    {
        // A rapid second right-click can hide the owned window before the Input
        // priority callback runs. Do not revive focus/IME state after it closed.
        if (!IsVisible || _hasClosed)
        {
            return;
        }

        // Do not steal a selection from either the input or a read-only todo
        // row when a delayed Input-priority focus request finally runs.
        if (IsKeyboardFocusWithin)
        {
            return;
        }

        TodoInput.Focus();
        Keyboard.Focus(TodoInput);
        TodoInput.Select(TodoInput.Text.Length, 0);
    }

    private void CopyCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (Keyboard.FocusedElement is not TextBox textBox)
        {
            e.CanExecute = false;
            return;
        }

        if (!IsCopySource(textBox))
        {
            return;
        }

        e.CanExecute = CanCopyFromTextBox(textBox);
        e.Handled = true;
    }

    private void CopyCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (Keyboard.FocusedElement is not TextBox textBox)
        {
            return;
        }

        var text = GetCopyText(textBox);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        CopyTextToClipboard(text);
        e.Handled = true;
    }

    private void TodoWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // TextBox's built-in Copy command disables itself when the selection
        // is empty before a parent CommandBinding can reliably replace that
        // behavior. Intercept the physical shortcut at the owned-window root:
        // input copies its full value without a selection, while read-only
        // rows still require an explicit text selection.
        if (e.Key != Key.C ||
            (Keyboard.Modifiers & ModifierKeys.Control) == 0 ||
            Keyboard.FocusedElement is not TextBox textBox ||
            !IsCopySource(textBox))
        {
            return;
        }

        var text = GetCopyText(textBox);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        CopyTextToClipboard(text);
        e.Handled = true;
    }

    private bool IsCopySource(TextBox textBox) =>
        ReferenceEquals(textBox, TodoInput) ||
        textBox is { IsReadOnly: true, DataContext: TodoItem };

    private bool CanCopyFromTextBox(TextBox textBox) =>
        !string.IsNullOrEmpty(GetCopyText(textBox));

    private string? GetCopyText(TextBox textBox)
    {
        if (ReferenceEquals(textBox, TodoInput))
        {
            return textBox.SelectionLength > 0
                ? textBox.SelectedText
                : textBox.Text;
        }

        return textBox is { IsReadOnly: true, DataContext: TodoItem } &&
               textBox.SelectionLength > 0
            ? textBox.SelectedText
            : null;
    }

    private void CopyTextToClipboard(string text)
    {
        try
        {
            Clipboard.SetDataObject(text, true);
            _pendingClipboardCopyText = null;
        }
        catch (ExternalException)
        {
            _pendingClipboardCopyText = text;
            if (_clipboardCopyRetryQueued)
            {
                return;
            }

            _clipboardCopyRetryQueued = true;
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                _retryClipboardCopyAction);
        }
    }

    private void RetryClipboardCopy()
    {
        _clipboardCopyRetryQueued = false;
        var text = _pendingClipboardCopyText;
        _pendingClipboardCopyText = null;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetDataObject(text, true);
        }
        catch (ExternalException)
        {
            // The clipboard can be held briefly by another process. One
            // deferred retry keeps Ctrl+C responsive without a blocking loop.
        }
    }

    public void SetPetSizeScale(double scale)
    {
        var normalizedScale = Math.Clamp(
            double.IsFinite(scale) ? scale : 1.0,
            0.75,
            1.40);
        _settingPetSizeScale = true;
        try
        {
            PetSizeSlider.Value = normalizedScale * 100;
            UpdatePetSizeLabel(PetSizeSlider.Value);
        }
        finally
        {
            _settingPetSizeScale = false;
        }
    }

    public void SetTailOnRight(bool tailOnRight)
    {
        if (_tailOnRight == tailOnRight)
        {
            return;
        }

        _tailOnRight = tailOnRight;
        FirstColumn.Width = new GridLength(tailOnRight ? 280 : 12);
        SecondColumn.Width = new GridLength(tailOnRight ? 12 : 280);
        Grid.SetColumn(TodoBorder, tailOnRight ? 0 : 1);
        Grid.SetColumn(TailHost, tailOnRight ? 1 : 0);
        TailHost.Margin = tailOnRight
            ? new Thickness(-1, 0, 0, 0)
            : new Thickness(0, 0, -1, 0);
        TailHost.HorizontalAlignment = tailOnRight
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        TailPolygon.Points = tailOnRight
            ? PointCollection.Parse("0,0 12,9 0,18")
            : PointCollection.Parse("12,0 0,9 12,18");
    }

    public void CloseForApplication()
    {
        _allowClose = true;
        if (!_hasClosed)
        {
            Close();
        }
    }

    public void AllowApplicationClose()
    {
        _allowClose = true;
    }

    private void TodoWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void TodoWindow_Closed(object? sender, EventArgs e)
    {
        _hasClosed = true;
        if (_petSizeAdjustmentActive)
        {
            EndPetSizeAdjustment();
        }
        else
        {
            FlushPendingPetSizeScaleChanged();
        }
    }

    private void TodoInput_PreviewTextInputStart(object sender, TextCompositionEventArgs e)
    {
        SetImeComposing(true);
    }

    private void TodoInput_PreviewTextInputUpdate(object sender, TextCompositionEventArgs e)
    {
        var composition = e.TextComposition;
        var hasCompositionText =
            !string.IsNullOrEmpty(composition.CompositionText) ||
            !string.IsNullOrEmpty(composition.SystemCompositionText);
        SetImeComposing(hasCompositionText);
    }

    private void TodoInput_PreviewTextInputCommitted(object sender, TextCompositionEventArgs e)
    {
        SetImeComposing(false);
    }

    private void TodoInput_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            _resetImeCompositionAfterFocusLossAction);
    }

    private void ResetImeCompositionAfterFocusLoss()
    {
        if (!TodoInput.IsKeyboardFocusWithin)
        {
            SetImeComposing(false);
        }
    }

    private void SetImeComposing(bool value)
    {
        if (IsImeComposing == value)
        {
            return;
        }

        IsImeComposing = value;
        ImeCompositionChanged?.Invoke(value);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        RequestAdd();
    }

    private void TodoInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || IsImeComposing)
        {
            return;
        }

        RequestAdd();
        e.Handled = true;
    }

    private void RequestAdd()
    {
        var text = TodoInput.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        AddRequested?.Invoke(text);
        TodoInput.Clear();
    }

    private void TodoCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: TodoItem item } checkBox)
        {
            return;
        }

        item.IsCompleted = checkBox.IsChecked == true;
        TodoChanged?.Invoke(item);
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TodoItem item })
        {
            DeleteRequested?.Invoke(item);
        }
    }

    private void PetSizeSlider_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e) => BeginPetSizeAdjustment();

    private void PetSizeSlider_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e) => EndPetSizeAdjustment();

    private void PetSizeSlider_LostMouseCapture(
        object sender,
        MouseEventArgs e) => EndPetSizeAdjustment();

    private void PetSizeSlider_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsPetSizeAdjustmentKey(e.Key))
        {
            BeginPetSizeAdjustment();
        }
    }

    private void PetSizeSlider_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (IsPetSizeAdjustmentKey(e.Key))
        {
            EndPetSizeAdjustment();
        }
    }

    private void PetSizeSlider_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e) => EndPetSizeAdjustment();

    private static bool IsPetSizeAdjustmentKey(Key key) =>
        key is Key.Left or Key.Right or Key.Up or Key.Down or
            Key.Home or Key.End or Key.PageUp or Key.PageDown;

    private void BeginPetSizeAdjustment()
    {
        if (_settingPetSizeScale || _petSizeAdjustmentActive)
        {
            return;
        }

        _petSizeAdjustmentActive = true;
        PetSizeAdjustmentStarted?.Invoke();
    }

    private void EndPetSizeAdjustment()
    {
        if (!_petSizeAdjustmentActive)
        {
            // Lost focus/capture and key-up can arrive in either order. Always
            // commit a value already sampled by ValueChanged, but only pair a
            // completion event with a matching start event.
            FlushPendingPetSizeScaleChanged();
            return;
        }

        FlushPendingPetSizeScaleChanged();
        _petSizeAdjustmentActive = false;
        PetSizeAdjustmentCompleted?.Invoke();
    }

    private void QueuePetSizeScaleChanged(double scale)
    {
        _pendingPetSizeScale = scale;
        _petSizeScaleNotificationQueued = true;
    }

    internal void FlushPendingPetSizeScaleChanged()
    {
        if (!_petSizeScaleNotificationQueued)
        {
            return;
        }

        var scale = _pendingPetSizeScale;
        _petSizeScaleNotificationQueued = false;
        PetSizeScaleChanged?.Invoke(scale);
    }


    private void PetSizeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (PetSizeLabel is null)
        {
            return;
        }

        UpdatePetSizeLabel(e.NewValue);

        if (!_settingPetSizeScale)
        {
            var scale = e.NewValue / 100;
            if (_petSizeAdjustmentActive)
            {
                QueuePetSizeScaleChanged(scale);
            }
            else
            {
                PetSizeScaleChanged?.Invoke(scale);
            }
        }
    }

    private void UpdatePetSizeLabel(double percentage)
    {
        var roundedPercentage = (int)Math.Round(percentage);
        if (_displayedPetSizePercent == roundedPercentage)
        {
            return;
        }

        _displayedPetSizePercent = roundedPercentage;
        PetSizeLabel.Text = $"{roundedPercentage}%";
    }
}
