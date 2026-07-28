using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace LubanDesktopPet;

public partial class TaskTextEditWindow : Window
{
    private readonly Action _commitAfterDeactivationAction;
    private readonly Action _completeImeDeactivationAction;
    private bool _allowClose;
    private bool _isImeComposing;
    private bool _commitAfterImeDeactivationPending;

    public TaskTextEditWindow(
        string title,
        string text,
        bool showAdvancedEdit)
    {
        InitializeComponent();
        _commitAfterDeactivationAction = CommitAfterDeactivation;
        _completeImeDeactivationAction = CompleteImeDeactivation;
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
        Deactivated += TaskTextEditWindow_Deactivated;
        Closing += TaskTextEditWindow_Closing;
        Closed += TaskTextEditWindow_Closed;
        Loaded += TaskTextEditWindow_Loaded;
    }

    public event Action<string>? TextAccepted;

    public event Action? AdvancedEditRequested;

    public void CloseWithoutSaving()
    {
        _allowClose = true;
        if (IsLoaded)
        {
            Close();
        }
    }

    private void TaskTextEditWindow_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        EditorTextBox.Focus();
        Keyboard.Focus(EditorTextBox);
        EditorTextBox.SelectAll();
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
            return;
        }

        if (e.Key == Key.Enter &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
            !_isImeComposing)
        {
            CommitAndClose(openAdvancedEditor: false);
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

        _allowClose = true;
        TextAccepted?.Invoke(normalized);
        Close();
        if (openAdvancedEditor)
        {
            AdvancedEditRequested?.Invoke();
        }

        return true;
    }

    private void TaskTextEditWindow_Deactivated(
        object? sender,
        EventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        if (_isImeComposing)
        {
            _commitAfterImeDeactivationPending = true;
            Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                _completeImeDeactivationAction);
            return;
        }

        QueueCommitAfterDeactivation();
    }

    private void TaskTextEditWindow_Activated(
        object? sender,
        EventArgs e)
    {
        _commitAfterImeDeactivationPending = false;
    }

    private void QueueCommitAfterDeactivation()
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            _commitAfterDeactivationAction);
    }

    private void CommitAfterDeactivation()
    {
        if (_allowClose ||
            _isImeComposing)
        {
            return;
        }

        if (IsActive || IsKeyboardFocusWithin)
        {
            _commitAfterImeDeactivationPending = false;
            return;
        }

        _commitAfterImeDeactivationPending = false;
        CommitAndClose(openAdvancedEditor: false);
    }

    private void CompleteImeDeactivation()
    {
        if (_allowClose ||
            !_commitAfterImeDeactivationPending)
        {
            return;
        }

        if (IsActive || IsKeyboardFocusWithin)
        {
            _commitAfterImeDeactivationPending = false;
            return;
        }

        // Losing the editor window ends the native IME session. Some input
        // methods report a final committed/update event, while others only
        // cancel the composition. ContextIdle runs after either path, so it is
        // safe to treat a still-pending composition as finished here.
        _isImeComposing = false;
        CommitAfterDeactivation();
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
        if (!_isImeComposing &&
            _commitAfterImeDeactivationPending)
        {
            QueueCommitAfterDeactivation();
        }
    }

    private void EditorTextBox_PreviewTextInputCommitted(
        object sender,
        TextCompositionEventArgs e)
    {
        _isImeComposing = false;
        if (_commitAfterImeDeactivationPending)
        {
            QueueCommitAfterDeactivation();
        }
    }

    private void TaskTextEditWindow_Closing(
        object? sender,
        CancelEventArgs e)
    {
        if (!_allowClose)
        {
            _allowClose = true;
        }
    }

    private void TaskTextEditWindow_Closed(object? sender, EventArgs e)
    {
        TextCompositionManager.RemovePreviewTextInputStartHandler(
            EditorTextBox,
            EditorTextBox_PreviewTextInputStart);
        TextCompositionManager.RemovePreviewTextInputUpdateHandler(
            EditorTextBox,
            EditorTextBox_PreviewTextInputUpdate);
        EditorTextBox.PreviewTextInput -=
            EditorTextBox_PreviewTextInputCommitted;
        Activated -= TaskTextEditWindow_Activated;
        Deactivated -= TaskTextEditWindow_Deactivated;
        Closing -= TaskTextEditWindow_Closing;
        Closed -= TaskTextEditWindow_Closed;
        Loaded -= TaskTextEditWindow_Loaded;
    }
}
