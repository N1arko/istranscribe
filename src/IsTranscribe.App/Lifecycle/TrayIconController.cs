using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using IsTranscribe.App.ManualControls;
using IsTranscribe.App.Strings;

namespace IsTranscribe.App.Lifecycle;

public sealed class TrayIconController : IDisposable
{
    private readonly Func<Task> _openAppAsync;
    private readonly Func<Task> _quitAsync;
    private readonly Func<bool> _notificationsEnabled;
    private readonly ManualControlGateway _manualControl;
    private readonly int _owningThreadId;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _startForceRecordItem;
    private readonly ToolStripMenuItem _stopCurrentRecordingItem;
    private readonly ToolStripMenuItem _privacyPauseItem;
    private readonly ToolStripMenuItem _openAppItem;
    private readonly ToolStripMenuItem _quitItem;
    private readonly EventHandler<ManualControlSnapshot> _snapshotChangedHandler;

    public TrayIconController(
        ManualControlGateway manualControl,
        Func<Task> openAppAsync,
        Func<Task> quitAsync,
        Func<bool> notificationsEnabled)
    {
        _manualControl = manualControl;
        _openAppAsync = openAppAsync;
        _quitAsync = quitAsync;
        _notificationsEnabled = notificationsEnabled;
        _owningThreadId = Environment.CurrentManagedThreadId;
        _synchronizationContext = SynchronizationContext.Current;

        _startForceRecordItem = new ToolStripMenuItem(LocalizationManager.Instance["Tray_StartForceRecord"], null, async (_, _) => await _manualControl.StartForceRecordAsync());
        _stopCurrentRecordingItem = new ToolStripMenuItem(LocalizationManager.Instance["Tray_StopCurrentRecording"], null, async (_, _) => await _manualControl.StopCurrentRecordingAsync());
        _privacyPauseItem = new ToolStripMenuItem(LocalizationManager.Instance["Tray_PrivacyPause_Off"], null, async (_, _) => await _manualControl.TogglePrivacyPauseAsync());

        var openAppItem = new ToolStripMenuItem(LocalizationManager.Instance["Tray_OpenApp"], null, async (_, _) => await _openAppAsync());
        var quitItem = new ToolStripMenuItem(LocalizationManager.Instance["Tray_Quit"], null, async (_, _) => await _quitAsync());
        _openAppItem = openAppItem;
        _quitItem = quitItem;
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _startForceRecordItem,
            _stopCurrentRecordingItem,
            _privacyPauseItem,
            new ToolStripSeparator(),
            openAppItem,
            quitItem
        ]);

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = SystemIcons.Application,
            Text = LocalizationManager.Instance["Tray_Tooltip_Default"],
            Visible = false
        };

        _notifyIcon.DoubleClick += async (_, _) => await _openAppAsync();
        _snapshotChangedHandler = (_, snapshot) => ApplySnapshot(snapshot);
        _manualControl.SnapshotChanged += _snapshotChangedHandler;
        LocalizationManager.Instance.LanguageChanged += (_, _) => RefreshMenuText();
    }

    // @spec spec://modules/platform/INFRA-001-windows-desktop-host-baseline#tray-window-lifecycle.tray
    public void Initialize()
    {
        ApplySnapshot(_manualControl.Snapshot);
        _notifyIcon.Visible = true;
    }

    public void Dispose()
    {
        _manualControl.SnapshotChanged -= _snapshotChangedHandler;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private void RefreshMenuText()
    {
        if (Environment.CurrentManagedThreadId != _owningThreadId && _synchronizationContext is not null)
        {
            _synchronizationContext.Post(_ => RefreshMenuText(), null);
            return;
        }

        _startForceRecordItem.Text = LocalizationManager.Instance["Tray_StartForceRecord"];
        _stopCurrentRecordingItem.Text = LocalizationManager.Instance["Tray_StopCurrentRecording"];
        _openAppItem.Text = LocalizationManager.Instance["Tray_OpenApp"];
        _quitItem.Text = LocalizationManager.Instance["Tray_Quit"];
        ApplySnapshot(_manualControl.Snapshot);
    }

    // @spec spec://modules/app/FEAT-003-manual-recording-controls-and-tray#tray
    private void ApplySnapshot(ManualControlSnapshot snapshot)
    {
        if (Environment.CurrentManagedThreadId != _owningThreadId && _synchronizationContext is not null)
        {
            _synchronizationContext.Post(_ => ApplySnapshot(snapshot), null);
            return;
        }

        _startForceRecordItem.Enabled = snapshot.CanStartForceRecord;
        _stopCurrentRecordingItem.Enabled = snapshot.CanStop;
        _privacyPauseItem.Text = snapshot.PrivacyPauseEnabled ? LocalizationManager.Instance["Tray_PrivacyPause_On"] : LocalizationManager.Instance["Tray_PrivacyPause_Off"];
        _notifyIcon.Icon = ResolveTrayIcon(snapshot);
        _notifyIcon.Text = BuildTrayText(snapshot);
    }

    // @spec spec://modules/app/FEAT-003-manual-recording-controls-and-tray#hotkeys.runtime
    public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (!_notificationsEnabled() || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (Environment.CurrentManagedThreadId != _owningThreadId && _synchronizationContext is not null)
        {
            _synchronizationContext.Post(_ => ShowNotification(title, message, icon), null);
            return;
        }

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(3000);
    }

    private static Icon ResolveTrayIcon(ManualControlSnapshot snapshot) => snapshot switch
    {
        { RecordingState: RecordingActivityState.Recording } => SystemIcons.Shield,
        { RecordingState: RecordingActivityState.Paused } => SystemIcons.Warning,
        { RecordingState: RecordingActivityState.Stopping } => SystemIcons.Information,
        { RecordingState: RecordingActivityState.AwaitingConfirmation } => SystemIcons.Question,
        { RecordingState: RecordingActivityState.OnboardingBlocked } => SystemIcons.Error,
        { PrivacyPauseEnabled: true } => SystemIcons.Exclamation,
        _ => SystemIcons.Application
    };

    private static string BuildTrayText(ManualControlSnapshot snapshot) => snapshot switch
    {
        { PrivacyPauseEnabled: true, RecordingState: RecordingActivityState.Idle } => LocalizationManager.Instance["Tray_Status_PrivacyPause"],
        { RecordingState: RecordingActivityState.AwaitingConfirmation } => LocalizationManager.Instance["Tray_Status_AwaitingConfirmation"],
        { RecordingState: RecordingActivityState.Recording } => LocalizationManager.Instance["Tray_Status_Recording"],
        { RecordingState: RecordingActivityState.Paused } => LocalizationManager.Instance["Tray_Status_Paused"],
        { RecordingState: RecordingActivityState.Stopping } => LocalizationManager.Instance["Tray_Status_Stopping"],
        { RecordingState: RecordingActivityState.OnboardingBlocked } => LocalizationManager.Instance["Tray_Status_SetupRequired"],
        _ => LocalizationManager.Instance["Tray_Tooltip_Default"]
    };
}
