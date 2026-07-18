using System;
using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LubanDesktopPet;

public partial class TodoWindow : Window
{
    private bool _settingAutoRoam;
    private bool _settingPetSizeScale;
    private bool _petSizeAdjustmentActive;
    private bool _tailOnRight = true;
    private bool _allowClose;
    private bool _hasClosed;

    public TodoWindow()
    {
        InitializeComponent();

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
        Closing += TodoWindow_Closing;
        Closed += (_, _) => _hasClosed = true;
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

    public event Action<bool>? AutoRoamChanged;

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
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            TodoInput.Focus();
            Keyboard.Focus(TodoInput);
            TodoInput.Select(TodoInput.Text.Length, 0);
        }));
    }

    public void SetAutoRoam(bool enabled)
    {
        _settingAutoRoam = true;
        try
        {
            AutoRoamToggle.IsChecked = enabled;
        }
        finally
        {
            _settingAutoRoam = false;
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
            PetSizeLabel.Text = $"{PetSizeSlider.Value:F0}%";
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
            new Action(() =>
            {
                if (!TodoInput.IsKeyboardFocusWithin)
                {
                    SetImeComposing(false);
                }
            }));
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

    private void AutoRoamToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingAutoRoam)
        {
            AutoRoamChanged?.Invoke(AutoRoamToggle.IsChecked == true);
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
            return;
        }

        _petSizeAdjustmentActive = false;
        PetSizeAdjustmentCompleted?.Invoke();
    }


    private void PetSizeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (PetSizeLabel is null)
        {
            return;
        }

        PetSizeLabel.Text = $"{e.NewValue:F0}%";
        if (!_settingPetSizeScale)
        {
            PetSizeScaleChanged?.Invoke(e.NewValue / 100);
        }
    }
}
