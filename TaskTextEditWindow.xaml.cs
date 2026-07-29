using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace LubanDesktopPet;

public partial class TaskTextEditWindow : Window
{
    private const double TargetEditorWidth = 378;
    private const double TargetEditorHeight = 414;

    private readonly Action _positionBesideOwnerAction;
    private readonly OwnedWindowPositioner.PositionCache _positionCache;
    private Window? _positionOwner;
    private bool _editorSizeInitialized;
    private bool _isImeComposing;
    private bool _positionBesideOwnerQueued;

    public TaskTextEditWindow(
        string title,
        string text,
        bool showAdvancedEdit)
    {
        InitializeComponent();
        _positionBesideOwnerAction = PositionBesideOwner;
        _positionCache = new OwnedWindowPositioner.PositionCache(this);
        EditorTitleText.Text = title;
        Title = title;
        EditorTextBox.Text = text;
        AdvancedEditButton.Visibility = showAdvancedEdit
            ? Visibility.Visible
            : Visibility.Collapsed;

        TextCompositionManager.AddPreviewTextInputStartHandler(
            EditorTextBox,
            EditorTextBox_PreviewTextInputStart);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(
            EditorTextBox,
            EditorTextBox_PreviewTextInputUpdate);
        EditorTextBox.PreviewTextInput +=
            EditorTextBox_PreviewTextInputCommitted;
        Activated += TaskTextEditWindow_Activated;
        Closed += TaskTextEditWindow_Closed;
        Loaded += TaskTextEditWindow_Loaded;
        SizeChanged += TaskTextEditWindow_SizeChanged;
        DpiChanged += TaskTextEditWindow_DpiChanged;
    }

    public event Action<string>? TextAccepted;

    public event Action? AdvancedEditRequested;

    public void CloseWithoutSaving()
    {
        if (IsLoaded)
        {
            Close();
        }
    }

    internal void RestoreAfterReminder()
    {
        if (!IsLoaded || !IsVisible)
        {
            return;
        }

        Activate();
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (!IsLoaded || !IsVisible)
                {
                    return;
                }

                EditorTextBox.Focus();
                Keyboard.Focus(EditorTextBox);
            }));
    }

    private void TaskTextEditWindow_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        AttachPositionOwner();
        ApplyEditorSizeForOwnerWorkArea();
        UpdateLayout();
        PositionBesideOwner();
        Opacity = 1;
        EditorTextBox.Focus();
        Keyboard.Focus(EditorTextBox);
        EditorTextBox.SelectAll();
    }

    private void AttachPositionOwner()
    {
        var owner = Owner;
        if (ReferenceEquals(_positionOwner, owner))
        {
            return;
        }

        DetachPositionOwner();
        _positionOwner = owner;
        if (_positionOwner is null)
        {
            return;
        }

        _positionOwner.LocationChanged += PositionOwner_GeometryChanged;
        _positionOwner.SizeChanged += PositionOwner_SizeChanged;
        _positionOwner.StateChanged += PositionOwner_GeometryChanged;
        _positionOwner.DpiChanged += PositionOwner_DpiChanged;
        _positionCache.InvalidateGeometry();
    }

    private void DetachPositionOwner()
    {
        if (_positionOwner is null)
        {
            return;
        }

        _positionOwner.LocationChanged -= PositionOwner_GeometryChanged;
        _positionOwner.SizeChanged -= PositionOwner_SizeChanged;
        _positionOwner.StateChanged -= PositionOwner_GeometryChanged;
        _positionOwner.DpiChanged -= PositionOwner_DpiChanged;
        _positionOwner = null;
        _positionCache.InvalidateGeometry();
    }

    private void ApplyEditorSizeForOwnerWorkArea()
    {
        var maximumWidth = double.PositiveInfinity;
        var maximumHeight = double.PositiveInfinity;
        if (_positionOwner is { } owner)
        {
            var workArea = MonitorWorkArea.GetForWindow(owner);
            maximumWidth = Math.Max(MinWidth, workArea.Width);
            maximumHeight = Math.Max(MinHeight, workArea.Height);
        }

        MaxWidth = maximumWidth;
        MaxHeight = maximumHeight;

        var requestedWidth = _editorSizeInitialized
            ? Width
            : TargetEditorWidth;
        var requestedHeight = _editorSizeInitialized
            ? Height
            : TargetEditorHeight;
        var width = Math.Clamp(
            double.IsFinite(requestedWidth) ? requestedWidth : TargetEditorWidth,
            MinWidth,
            maximumWidth);
        var height = Math.Clamp(
            double.IsFinite(requestedHeight) ? requestedHeight : TargetEditorHeight,
            MinHeight,
            maximumHeight);
        _editorSizeInitialized = true;

        if (Math.Abs(Width - width) >= 0.1)
        {
            Width = width;
        }

        if (Math.Abs(Height - height) >= 0.1)
        {
            Height = height;
        }
    }

    private void SchedulePositionBesideOwner()
    {
        if (_positionBesideOwnerQueued || !IsVisible)
        {
            return;
        }

        _positionBesideOwnerQueued = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            _positionBesideOwnerAction);
    }

    private void PositionBesideOwner()
    {
        _positionBesideOwnerQueued = false;
        if (_positionOwner is not { IsLoaded: true } owner ||
            !IsVisible)
        {
            return;
        }

        ApplyEditorSizeForOwnerWorkArea();
        UpdateLayout();
        if (!OwnedWindowPositioner.TryPosition(
                owner,
                this,
                _positionCache,
                out _))
        {
            PositionBesideOwnerFallback(owner);
        }
    }

    private void PositionBesideOwnerFallback(Window owner)
    {
        var workArea = MonitorWorkArea.GetForWindow(owner);
        var ownerWidth = owner.ActualWidth > 0
            ? owner.ActualWidth
            : owner.Width;
        var ownerHeight = owner.ActualHeight > 0
            ? owner.ActualHeight
            : owner.Height;
        var leftCandidate = owner.Left - Width;
        var desiredLeft = leftCandidate >= workArea.Left
            ? leftCandidate
            : owner.Left + ownerWidth;
        var desiredTop = owner.Top + ownerHeight - Height;
        var maximumLeft = Math.Max(
            workArea.Left,
            workArea.Right - Width);
        var maximumTop = Math.Max(
            workArea.Top,
            workArea.Bottom - Height);

        Left = Math.Clamp(
            desiredLeft,
            workArea.Left,
            maximumLeft);
        Top = Math.Clamp(
            desiredTop,
            workArea.Top,
            maximumTop);
    }

    private void PositionOwner_GeometryChanged(
        object? sender,
        EventArgs e)
    {
        SchedulePositionBesideOwner();
    }

    private void PositionOwner_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        _positionCache.InvalidateGeometry();
        SchedulePositionBesideOwner();
    }

    private void PositionOwner_DpiChanged(
        object sender,
        DpiChangedEventArgs e)
    {
        _positionCache.InvalidateGeometry();
        SchedulePositionBesideOwner();
    }

    private void TaskTextEditWindow_DpiChanged(
        object sender,
        DpiChangedEventArgs e)
    {
        _positionCache.InvalidateGeometry();
        SchedulePositionBesideOwner();
    }

    private void TaskTextEditWindow_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        _positionCache.InvalidateGeometry();
    }

    private void EditorTextBox_TextChanged(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        CharacterCountText.Text =
            $"{EditorTextBox.Text.Length}/5000";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        CommitAndClose(openAdvancedEditor: false);
        e.Handled = true;
    }

    private void AdvancedEditButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        CommitAndClose(openAdvancedEditor: true);
        e.Handled = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseWithoutSaving();
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_isImeComposing)
        {
            CloseWithoutSaving();
            e.Handled = true;
        }
    }

    private bool CommitAndClose(bool openAdvancedEditor)
    {
        var normalized = EditorTextBox.Text.Trim();
        if (normalized.Length == 0)
        {
            System.Media.SystemSounds.Beep.Play();
            EditorTextBox.Focus();
            return false;
        }

        TextAccepted?.Invoke(normalized);
        Close();
        if (openAdvancedEditor)
        {
            AdvancedEditRequested?.Invoke();
        }

        return true;
    }

    private void EditorTextBox_PreviewTextInputStart(
        object sender,
        TextCompositionEventArgs e)
    {
        _isImeComposing = true;
    }

    private void EditorTextBox_PreviewTextInputUpdate(
        object sender,
        TextCompositionEventArgs e)
    {
        var composition = e.TextComposition;
        _isImeComposing =
            !string.IsNullOrEmpty(composition.CompositionText) ||
            !string.IsNullOrEmpty(composition.SystemCompositionText);
    }

    private void EditorTextBox_PreviewTextInputCommitted(
        object sender,
        TextCompositionEventArgs e)
    {
        _isImeComposing = false;
    }

    private void TaskTextEditWindow_Activated(
        object? sender,
        EventArgs e)
    {
        // Some IMEs cancel their native composition on deactivation without
        // sending WPF a final committed/update event. Returning to the editor
        // starts a fresh input session, so an old composing flag must not make
        // Esc permanently ineffective.
        _isImeComposing = false;
    }

    private void TaskTextEditWindow_Closed(object? sender, EventArgs e)
    {
        DetachPositionOwner();
        TextCompositionManager.RemovePreviewTextInputStartHandler(
            EditorTextBox,
            EditorTextBox_PreviewTextInputStart);
        TextCompositionManager.RemovePreviewTextInputUpdateHandler(
            EditorTextBox,
            EditorTextBox_PreviewTextInputUpdate);
        EditorTextBox.PreviewTextInput -=
            EditorTextBox_PreviewTextInputCommitted;
        Activated -= TaskTextEditWindow_Activated;
        Closed -= TaskTextEditWindow_Closed;
        Loaded -= TaskTextEditWindow_Loaded;
        SizeChanged -= TaskTextEditWindow_SizeChanged;
        DpiChanged -= TaskTextEditWindow_DpiChanged;
    }
}
