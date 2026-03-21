using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AlienFxLite.Contracts;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;
using MediaBrushes = System.Windows.Media.Brushes;
using WinForms = System.Windows.Forms;
using WpfCursors = System.Windows.Input.Cursors;
using WpfButton = System.Windows.Controls.Button;
using WpfMessageBox = System.Windows.MessageBox;
using WpfPoint = System.Windows.Point;

namespace AlienFxLite.UI;

public partial class MainWindow : Window
{
    private static readonly MediaColor Accent = MediaColor.FromRgb(94, 220, 255);
    private static readonly MediaColor AccentStrong = MediaColor.FromRgb(124, 255, 228);
    private static readonly MediaColor Danger = MediaColor.FromRgb(255, 122, 156);

    private static readonly IReadOnlyDictionary<LightingZone, string> ZoneNames = new Dictionary<LightingZone, string>
    {
        [LightingZone.KbLeft] = "KB Left",
        [LightingZone.KbCenter] = "KB Center",
        [LightingZone.KbRight] = "KB Right",
        [LightingZone.KbNumPad] = "KB NumPad",
    };

    private static readonly LightingZone?[] KeyboardDeck =
    [
        LightingZone.KbLeft, LightingZone.KbLeft, LightingZone.KbLeft, LightingZone.KbLeft, LightingZone.KbLeft, LightingZone.KbLeft,
        LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter,
        LightingZone.KbRight, LightingZone.KbRight, LightingZone.KbRight, LightingZone.KbRight, LightingZone.KbRight,
        LightingZone.KbNumPad, LightingZone.KbNumPad, LightingZone.KbNumPad, LightingZone.KbNumPad,

        LightingZone.KbLeft, LightingZone.KbLeft, LightingZone.KbLeft, LightingZone.KbLeft, LightingZone.KbLeft,
        LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter,
        LightingZone.KbRight, LightingZone.KbRight, LightingZone.KbRight, LightingZone.KbRight, LightingZone.KbRight,
        LightingZone.KbNumPad, LightingZone.KbNumPad, LightingZone.KbNumPad, LightingZone.KbNumPad,

        LightingZone.KbLeft, LightingZone.KbLeft, LightingZone.KbLeft, LightingZone.KbLeft, LightingZone.KbLeft,
        LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter,
        LightingZone.KbRight, LightingZone.KbRight, LightingZone.KbRight, LightingZone.KbRight,
        LightingZone.KbNumPad, LightingZone.KbNumPad, LightingZone.KbNumPad, LightingZone.KbNumPad,

        LightingZone.KbLeft, LightingZone.KbLeft, LightingZone.KbLeft, LightingZone.KbLeft, LightingZone.KbLeft, LightingZone.KbLeft,
        LightingZone.KbCenter, LightingZone.KbCenter, null, LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter,
        LightingZone.KbRight, LightingZone.KbRight, LightingZone.KbRight, LightingZone.KbRight,
        LightingZone.KbNumPad, LightingZone.KbNumPad, LightingZone.KbNumPad, LightingZone.KbNumPad,

        LightingZone.KbLeft, LightingZone.KbLeft, LightingZone.KbLeft, LightingZone.KbLeft, LightingZone.KbLeft,
        LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter,
        LightingZone.KbRight, LightingZone.KbRight, LightingZone.KbRight, LightingZone.KbRight,
        LightingZone.KbNumPad, LightingZone.KbNumPad, LightingZone.KbNumPad, LightingZone.KbNumPad,

        LightingZone.KbLeft, LightingZone.KbLeft, LightingZone.KbLeft, LightingZone.KbLeft,
        LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter, LightingZone.KbCenter,
        LightingZone.KbRight, LightingZone.KbRight, LightingZone.KbRight, LightingZone.KbRight,
        LightingZone.KbNumPad, LightingZone.KbNumPad, LightingZone.KbNumPad, LightingZone.KbNumPad,
    ];

    private readonly AlienFxLiteServiceClient _client = new();
    private readonly DesktopSettingsStore _desktopSettingsStore = new();
    private readonly GitHubReleaseUpdateService _updateService = new();
    private readonly AppLaunchOptions _launchOptions;
    private readonly WinForms.ColorDialog _colorDialog = new() { FullOpen = true };
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Dictionary<LightingZone, ToggleButton> _zoneButtons;
    private readonly List<KeyCapCell> _keyboardCells = [];
    private readonly WinForms.NotifyIcon _notifyIcon;

    private DesktopSettings _desktopSettings;
    private DrawingColor _primaryColor = DrawingColor.White;
    private DrawingColor _secondaryColor = DrawingColor.Black;
    private bool _refreshing;
    private bool _loadingLightingState;
    private bool _loadingDesktopSettings;
    private bool _lightingDirty;
    private bool _allowExit;
    private bool _checkingForUpdates;
    private bool _startHiddenPending;
    private bool _trayTipShown;
    private string? _desktopSettingsError;
    private string? _updateError;
    private GitHubReleaseUpdateService.UpdateCheckResult? _lastUpdateResult;

    public MainWindow()
        : this(new AppLaunchOptions(AppCommand.Ui, false, null, null))
    {
    }

    internal MainWindow(AppLaunchOptions launchOptions)
    {
        _launchOptions = launchOptions;
        _desktopSettings = _desktopSettingsStore.Load();

        try
        {
            _desktopSettingsStore.SyncStartupRegistration(_desktopSettings.StartWithWindows);
        }
        catch (Exception ex)
        {
            _desktopSettingsError = ex.Message;
        }

        InitializeComponent();
        _notifyIcon = CreateNotifyIcon();

        _zoneButtons = new Dictionary<LightingZone, ToggleButton>
        {
            [LightingZone.KbLeft] = LeftZoneButton,
            [LightingZone.KbCenter] = CenterZoneButton,
            [LightingZone.KbRight] = RightZoneButton,
            [LightingZone.KbNumPad] = NumPadZoneButton,
        };

        BuildKeyboardDeck();

        _loadingLightingState = true;
        foreach (ToggleButton zoneButton in _zoneButtons.Values)
        {
            zoneButton.IsChecked = true;
        }

        EffectCombo.ItemsSource = Enum.GetValues<LightingEffect>();
        EffectCombo.SelectedItem = LightingEffect.Static;
        SpeedSlider.Value = 50;
        BrightnessSlider.Value = 100;
        KeepAliveCheck.IsChecked = true;
        EnabledCheck.IsChecked = true;
        _loadingLightingState = false;

        _loadingDesktopSettings = true;
        StartWithWindowsCheck.IsChecked = _desktopSettings.StartWithWindows;
        MinimizeToTrayCheck.IsChecked = _desktopSettings.MinimizeToTray;
        _loadingDesktopSettings = false;

        CurrentVersionText.Text = $"AlienFx Lite {AppVersionInfo.CurrentVersion}";
        UpdateColorButton(PrimaryColorButton, _primaryColor);
        UpdateColorButton(SecondaryColorButton, _secondaryColor);
        UpdateTrackLabels();
        UpdateEffectUi();
        UpdateApplyButtonState();
        UpdateDesktopStatus();
        UpdateUpdateUi();
        RefreshKeyboardPreview();
        UpdateTrayVisibility();

        if (_launchOptions.StartupLaunch && _desktopSettings.MinimizeToTray)
        {
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
            _startHiddenPending = true;
        }

        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync(silent: true, preservePendingLighting: true).ConfigureAwait(true);
        StateChanged += Window_StateChanged;
        Closing += Window_Closing;
        Closed += Window_Closed;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync(silent: true, preservePendingLighting: false).ConfigureAwait(true);
        _refreshTimer.Start();

        if (_startHiddenPending)
        {
            HideToTray(showNotification: false);
            _startHiddenPending = false;
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        BackdropHelper.TryApply(this);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync(silent: false, preservePendingLighting: false).ConfigureAwait(true);
    }

    private async void ApplyLightingButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            List<LightingZone> zones = _zoneButtons.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList();
            if (zones.Count == 0)
            {
                WpfMessageBox.Show(this, "Select at least one keyboard zone.", "AlienFx Lite", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            LightingEffect effect = (LightingEffect)(EffectCombo.SelectedItem ?? LightingEffect.Static);
            SetLightingStateRequest request = new(
                zones,
                effect,
                ToRgb(_primaryColor),
                effect == LightingEffect.Morph ? ToRgb(_secondaryColor) : null,
                (int)SpeedSlider.Value,
                (int)BrightnessSlider.Value,
                KeepAliveCheck.IsChecked,
                EnabledCheck.IsChecked);

            await _client.SetLightingStateAsync(request).ConfigureAwait(true);
            _lightingDirty = false;
            UpdateApplyButtonState();
            await RefreshStatusAsync(silent: true, preservePendingLighting: false).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this, ex.Message, "AlienFx Lite", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void FanAutoButton_Click(object sender, RoutedEventArgs e) =>
        await ApplyFanModeAsync(FanControlMode.Auto).ConfigureAwait(true);

    private async void FanMaxButton_Click(object sender, RoutedEventArgs e) =>
        await ApplyFanModeAsync(FanControlMode.Max).ConfigureAwait(true);

    private void PrimaryColorButton_Click(object sender, RoutedEventArgs e) => ChooseColor(primary: true);

    private void SecondaryColorButton_Click(object sender, RoutedEventArgs e) => ChooseColor(primary: false);

    private void EffectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateEffectUi();
        MarkLightingDirty();
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateTrackLabels();
        MarkLightingDirty();
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateTrackLabels();
        MarkLightingDirty();
    }

    private void SettingChanged(object sender, RoutedEventArgs e) => MarkLightingDirty();

    private void ZoneButton_Changed(object sender, RoutedEventArgs e) => MarkLightingDirty();

    private void DesktopSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingDesktopSettings)
        {
            return;
        }

        DesktopSettings next = new(
            StartWithWindowsCheck.IsChecked == true,
            MinimizeToTrayCheck.IsChecked == true);

        try
        {
            _desktopSettingsStore.Save(next);
            _desktopSettings = next;
            _desktopSettingsError = null;
            UpdateTrayVisibility();
            UpdateDesktopStatus();
        }
        catch (Exception ex)
        {
            _desktopSettingsError = ex.Message;
            ApplyDesktopSettingsToControls(_desktopSettings);
            UpdateDesktopStatus();
            WpfMessageBox.Show(this, ex.Message, "AlienFx Lite", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingForUpdates)
        {
            return;
        }

        _checkingForUpdates = true;
        _updateError = null;
        UpdateUpdateUi();

        try
        {
            _lastUpdateResult = await _updateService.CheckAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _lastUpdateResult = null;
            _updateError = ex.Message;
        }
        finally
        {
            _checkingForUpdates = false;
            UpdateUpdateUi();
        }
    }

    private void OpenReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        string? releaseUrl = _lastUpdateResult?.Release?.ReleasePageUrl;
        if (!string.IsNullOrWhiteSpace(releaseUrl))
        {
            OpenExternalTarget(releaseUrl);
        }
    }

    private void DownloadInstallerButton_Click(object sender, RoutedEventArgs e)
    {
        string? installerUrl = _lastUpdateResult?.Release?.InstallerUrl;
        if (!string.IsNullOrWhiteSpace(installerUrl))
        {
            OpenExternalTarget(installerUrl);
        }
    }

    private async Task RefreshStatusAsync(bool silent, bool preservePendingLighting)
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            StatusSnapshot status = await _client.GetStatusAsync().ConfigureAwait(true);
            ApplyStatus(status, preservePendingLighting && _lightingDirty);
        }
        catch (Exception ex)
        {
            ServiceStatusText.Text = "Broker unavailable";
            ServiceStatusText.Foreground = new SolidColorBrush(Danger);
            DeviceStatusText.Text = ex.Message;
            FanModeText.Text = "Mode: unavailable";
            FanRpmText.Text = "RPM: n/a";
            LightingHintText.Text = "The broker is offline, so keyboard changes cannot be applied right now.";
            FanHintText.Text = "Fan control requires the broker service.";
            PendingText.Text = "Broker unavailable.";
            PendingText.Foreground = new SolidColorBrush(Danger);

            if (!silent)
            {
                WpfMessageBox.Show(this, ex.Message, "AlienFx Lite", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task ApplyFanModeAsync(FanControlMode mode)
    {
        try
        {
            await _client.SetFanModeAsync(new SetFanModeRequest(mode)).ConfigureAwait(true);
            await RefreshStatusAsync(silent: true, preservePendingLighting: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this, ex.Message, "AlienFx Lite", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyStatus(StatusSnapshot status, bool preservePendingLighting)
    {
        ServiceStatusText.Text = "Broker connected";
        ServiceStatusText.Foreground = new SolidColorBrush(AccentStrong);
        DeviceStatusText.Text = $"Lighting: {(status.Devices.LightingAvailable ? "online" : "offline")}  |  Fans: {(status.Devices.FanAvailable ? "online" : "offline")}";
        FanModeText.Text = $"Mode: {status.Fan.Mode}";
        FanRpmText.Text = status.Fan.Rpm.Count > 0
            ? $"RPM: {string.Join(" / ", status.Fan.Rpm)}"
            : $"RPM: n/a ({status.Fan.Message})";
        FanHintText.Text = status.Fan.Message;

        if (!preservePendingLighting)
        {
            _loadingLightingState = true;
            try
            {
                BrightnessSlider.Value = status.Lighting.Brightness;
                KeepAliveCheck.IsChecked = status.Lighting.KeepAlive;
                EnabledCheck.IsChecked = status.Lighting.Enabled;

                ZoneLightingState? reference = status.Lighting.ZoneStates.OrderBy(static zone => zone.Zone).FirstOrDefault();
                if (reference is not null)
                {
                    EffectCombo.SelectedItem = reference.Effect;
                    SpeedSlider.Value = reference.Speed;
                    _primaryColor = FromRgb(reference.PrimaryColor);
                    _secondaryColor = reference.SecondaryColor is null ? DrawingColor.Black : FromRgb(reference.SecondaryColor.Value);
                    UpdateColorButton(PrimaryColorButton, _primaryColor);
                    UpdateColorButton(SecondaryColorButton, _secondaryColor);
                }

                HashSet<LightingZone> activeZones = status.Lighting.ZoneStates
                    .Where(static zone => zone.Enabled)
                    .Select(static zone => zone.Zone)
                    .ToHashSet();
                foreach ((LightingZone zone, ToggleButton toggle) in _zoneButtons)
                {
                    toggle.IsChecked = activeZones.Contains(zone);
                }
            }
            finally
            {
                _loadingLightingState = false;
            }

            _lightingDirty = false;
        }

        UpdateTrackLabels();
        UpdateEffectUi();
        UpdateApplyButtonState();
        RefreshKeyboardPreview();
    }

    private void BuildKeyboardDeck()
    {
        KeyboardMatrixPanel.Children.Clear();
        _keyboardCells.Clear();

        foreach (LightingZone? zone in KeyboardDeck)
        {
            Border keyCap = new()
            {
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                MinHeight = 22,
            };

            if (zone is null)
            {
                keyCap.Opacity = 0;
                keyCap.IsHitTestVisible = false;
                KeyboardMatrixPanel.Children.Add(keyCap);
                continue;
            }

            keyCap.Tag = zone.Value;
            keyCap.Cursor = WpfCursors.Hand;
            keyCap.MouseLeftButtonUp += KeyboardCell_MouseLeftButtonUp;
            KeyboardMatrixPanel.Children.Add(keyCap);
            _keyboardCells.Add(new KeyCapCell(zone.Value, keyCap));
        }
    }

    private void KeyboardCell_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: LightingZone zone })
        {
            return;
        }

        _zoneButtons[zone].IsChecked = _zoneButtons[zone].IsChecked != true;
        e.Handled = true;
    }

    private void ChooseColor(bool primary)
    {
        _colorDialog.Color = primary ? _primaryColor : _secondaryColor;
        if (_colorDialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        if (primary)
        {
            _primaryColor = _colorDialog.Color;
            UpdateColorButton(PrimaryColorButton, _primaryColor);
        }
        else
        {
            _secondaryColor = _colorDialog.Color;
            UpdateColorButton(SecondaryColorButton, _secondaryColor);
        }

        MarkLightingDirty();
    }

    private void MarkLightingDirty()
    {
        if (_loadingLightingState)
        {
            return;
        }

        _lightingDirty = true;
        UpdateApplyButtonState();
        RefreshKeyboardPreview();
    }

    private void UpdateApplyButtonState()
    {
        ApplyButtonTitleText.Text = _lightingDirty ? "Apply Lighting" : "Reapply Lighting";
        ApplyButtonHintText.Text = _lightingDirty
            ? "Commit pending deck changes to the keyboard and saved broker state."
            : "Force the current saved deck back onto the keyboard if firmware drifted.";

        PendingText.Text = _lightingDirty
            ? "Pending local edits. Press Apply Lighting to commit them."
            : "Keyboard deck matches the saved broker state.";
        PendingText.Foreground = new SolidColorBrush(_lightingDirty ? Accent : Colors.White);
    }

    private void UpdateEffectUi()
    {
        LightingEffect effect = (LightingEffect)(EffectCombo.SelectedItem ?? LightingEffect.Static);
        SecondaryPanel.Visibility = effect == LightingEffect.Morph ? Visibility.Visible : Visibility.Collapsed;
        SpeedSlider.IsEnabled = effect != LightingEffect.Static;
        SpeedValueText.IsEnabled = effect != LightingEffect.Static;
    }

    private void UpdateTrackLabels()
    {
        SpeedValueText.Text = $"{(int)SpeedSlider.Value}%";
        BrightnessValueText.Text = $"{(int)BrightnessSlider.Value}%";
    }

    private void RefreshKeyboardPreview()
    {
        LightingEffect effect = (LightingEffect)(EffectCombo.SelectedItem ?? LightingEffect.Static);
        MediaColor primary = MediaColor.FromRgb(_primaryColor.R, _primaryColor.G, _primaryColor.B);
        MediaColor secondary = MediaColor.FromRgb(_secondaryColor.R, _secondaryColor.G, _secondaryColor.B);
        bool enabled = EnabledCheck.IsChecked != false;

        foreach (KeyCapCell cell in _keyboardCells)
        {
            bool selected = _zoneButtons[cell.Zone].IsChecked == true;
            cell.Element.Background = BuildKeyBrush(selected, enabled, effect, primary, secondary);
            cell.Element.BorderBrush = new SolidColorBrush(selected
                ? WithAlpha(Lighten(primary, enabled ? 0.22 : 0.08), enabled ? (byte)230 : (byte)110)
                : MediaColor.FromArgb(92, 76, 97, 125));
            cell.Element.Opacity = selected ? (enabled ? 1 : 0.74) : 0.94;
        }

        string selectedZones = string.Join(", ", _zoneButtons.Where(pair => pair.Value.IsChecked == true).Select(pair => ZoneNames[pair.Key]));
        if (string.IsNullOrWhiteSpace(selectedZones))
        {
            selectedZones = "No zones selected";
        }

        LightingHintText.Text =
            $"Selected: {selectedZones}  |  Effect: {EffectCombo.SelectedItem}  |  Brightness: {(int)BrightnessSlider.Value}%  |  KeepAlive: {(KeepAliveCheck.IsChecked == true ? "on" : "off")}";
    }

    private void ApplyDesktopSettingsToControls(DesktopSettings settings)
    {
        _loadingDesktopSettings = true;
        try
        {
            StartWithWindowsCheck.IsChecked = settings.StartWithWindows;
            MinimizeToTrayCheck.IsChecked = settings.MinimizeToTray;
        }
        finally
        {
            _loadingDesktopSettings = false;
        }
    }

    private void UpdateDesktopStatus()
    {
        List<string> fragments = [];

        if (_desktopSettings.StartWithWindows)
        {
            fragments.Add("Launches automatically at sign-in.");
        }
        else
        {
            fragments.Add("Launch remains manual.");
        }

        if (_desktopSettings.MinimizeToTray)
        {
            fragments.Add("Minimize or close keeps the app in the tray.");
        }
        else
        {
            fragments.Add("Closing exits the app fully.");
        }

        if (!string.IsNullOrWhiteSpace(_desktopSettingsError))
        {
            fragments.Add($"Startup registration warning: {_desktopSettingsError}");
        }

        DesktopStatusText.Text = string.Join(" ", fragments);
        UpdateTrayVisibility();
    }

    private void UpdateUpdateUi()
    {
        CurrentVersionText.Text = $"AlienFx Lite {AppVersionInfo.CurrentVersion}";
        CheckUpdatesButton.Content = _checkingForUpdates ? "Checking..." : "Check for updates";
        CheckUpdatesButton.IsEnabled = !_checkingForUpdates;

        if (_checkingForUpdates)
        {
            UpdateStatusText.Text = "Checking GitHub Releases for a newer build...";
            UpdateWarningText.Visibility = Visibility.Collapsed;
            OpenReleaseButton.Visibility = Visibility.Collapsed;
            DownloadInstallerButton.Visibility = Visibility.Collapsed;
            ReleaseNotesText.Visibility = Visibility.Collapsed;
            return;
        }

        if (!string.IsNullOrWhiteSpace(_updateError))
        {
            UpdateStatusText.Text = "Unable to complete the update check.";
            UpdateWarningText.Text = _updateError;
            UpdateWarningText.Visibility = Visibility.Visible;
            OpenReleaseButton.Visibility = Visibility.Collapsed;
            DownloadInstallerButton.Visibility = Visibility.Collapsed;
            ReleaseNotesText.Visibility = Visibility.Collapsed;
            return;
        }

        if (_lastUpdateResult is null)
        {
            UpdateStatusText.Text = "Manual update checks query GitHub Releases only when you click the button.";
            UpdateWarningText.Visibility = Visibility.Collapsed;
            OpenReleaseButton.Visibility = Visibility.Collapsed;
            DownloadInstallerButton.Visibility = Visibility.Collapsed;
            ReleaseNotesText.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateStatusText.Text = _lastUpdateResult.StatusMessage;
        if (!string.IsNullOrWhiteSpace(_lastUpdateResult.WarningMessage))
        {
            UpdateWarningText.Text = _lastUpdateResult.WarningMessage;
            UpdateWarningText.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateWarningText.Visibility = Visibility.Collapsed;
        }

        OpenReleaseButton.Visibility = _lastUpdateResult.Release is null ? Visibility.Collapsed : Visibility.Visible;
        DownloadInstallerButton.Visibility = _lastUpdateResult.UpdateAvailable &&
                                             !string.IsNullOrWhiteSpace(_lastUpdateResult.Release?.InstallerUrl)
            ? Visibility.Visible
            : Visibility.Collapsed;

        string notes = SummarizeReleaseNotes(_lastUpdateResult.Release?.Notes);
        if (string.IsNullOrWhiteSpace(notes))
        {
            ReleaseNotesText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ReleaseNotesText.Text = notes;
            ReleaseNotesText.Visibility = Visibility.Visible;
        }
    }

    private WinForms.NotifyIcon CreateNotifyIcon()
    {
        WinForms.ContextMenuStrip menu = new();
        menu.Items.Add("Open", null, TrayOpen_Click);
        menu.Items.Add("Refresh", null, TrayRefresh_Click);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Fans: BIOS / AUTO", null, TrayFanAuto_Click);
        menu.Items.Add("Fans: MAX", null, TrayFanMax_Click);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, TrayExit_Click);

        WinForms.NotifyIcon notifyIcon = new()
        {
            Text = "AlienFx Lite",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = false,
        };

        notifyIcon.DoubleClick += TrayOpen_Click;
        return notifyIcon;
    }

    private void UpdateTrayVisibility()
    {
        _notifyIcon.Visible = _desktopSettings.MinimizeToTray;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (_desktopSettings.MinimizeToTray && WindowState == WindowState.Minimized)
        {
            HideToTray(showNotification: !_startHiddenPending);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowExit || !_desktopSettings.MinimizeToTray)
        {
            return;
        }

        e.Cancel = true;
        HideToTray(showNotification: false);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private void HideToTray(bool showNotification)
    {
        ShowInTaskbar = false;
        Hide();
        UpdateTrayVisibility();

        if (showNotification && !_trayTipShown)
        {
            _notifyIcon.BalloonTipTitle = "AlienFx Lite";
            _notifyIcon.BalloonTipText = "AlienFx Lite is still running in the tray. Double-click the icon to reopen it.";
            _notifyIcon.ShowBalloonTip(1500);
            _trayTipShown = true;
        }
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        UpdateTrayVisibility();
    }

    private void TrayOpen_Click(object? sender, EventArgs e) =>
        Dispatcher.Invoke(RestoreFromTray);

    private void TrayRefresh_Click(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() => _ = RefreshStatusAsync(silent: true, preservePendingLighting: true));

    private void TrayFanAuto_Click(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() => _ = ApplyFanModeAsync(FanControlMode.Auto));

    private void TrayFanMax_Click(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() => _ = ApplyFanModeAsync(FanControlMode.Max));

    private void TrayExit_Click(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _allowExit = true;
            Close();
        });
    }

    private static string SummarizeReleaseNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return string.Empty;
        }

        string[] lines = notes
            .Replace("\r", string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(5)
            .ToArray();

        string summary = string.Join(Environment.NewLine, lines);
        return summary.Length > 420 ? summary[..420].TrimEnd() + "..." : summary;
    }

    private static void OpenExternalTarget(string target)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true,
        });
    }

    private static System.Windows.Media.Brush BuildKeyBrush(bool selected, bool enabled, LightingEffect effect, MediaColor primary, MediaColor secondary)
    {
        if (!selected)
        {
            return new LinearGradientBrush(
                MediaColor.FromArgb(176, 30, 42, 58),
                MediaColor.FromArgb(208, 12, 18, 27),
                new WpfPoint(0, 0),
                new WpfPoint(1, 1));
        }

        if (!enabled)
        {
            return new LinearGradientBrush(
                MediaColor.FromArgb(118, 56, 71, 92),
                MediaColor.FromArgb(150, 21, 29, 40),
                new WpfPoint(0, 0),
                new WpfPoint(1, 1));
        }

        if (effect == LightingEffect.Morph)
        {
            return new LinearGradientBrush(
                WithAlpha(Lighten(primary, 0.18), 224),
                WithAlpha(Lighten(secondary, 0.12), 206),
                new WpfPoint(0, 0),
                new WpfPoint(1, 1));
        }

        return new LinearGradientBrush(
            WithAlpha(Lighten(primary, 0.18), effect == LightingEffect.Pulse ? (byte)212 : (byte)232),
            WithAlpha(Darken(primary, 0.12), effect == LightingEffect.Pulse ? (byte)182 : (byte)214),
            new WpfPoint(0, 0),
            new WpfPoint(1, 1));
    }

    private static void UpdateColorButton(WpfButton button, DrawingColor color)
    {
        button.Content = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        button.Background = new SolidColorBrush(MediaColor.FromRgb(color.R, color.G, color.B));
        button.BorderBrush = new SolidColorBrush(color.GetBrightness() < 0.45f ? AccentStrong : MediaColor.FromRgb(19, 28, 39));
        button.Foreground = color.GetBrightness() < 0.45f ? MediaBrushes.White : MediaBrushes.Black;
    }

    private static MediaColor WithAlpha(MediaColor color, byte alpha) => MediaColor.FromArgb(alpha, color.R, color.G, color.B);

    private static MediaColor Lighten(MediaColor color, double amount)
    {
        byte Blend(byte channel) => (byte)Math.Clamp(channel + ((255 - channel) * amount), 0, 255);
        return MediaColor.FromRgb(Blend(color.R), Blend(color.G), Blend(color.B));
    }

    private static MediaColor Darken(MediaColor color, double amount)
    {
        byte Blend(byte channel) => (byte)Math.Clamp(channel * (1 - amount), 0, 255);
        return MediaColor.FromRgb(Blend(color.R), Blend(color.G), Blend(color.B));
    }

    private static RgbColor ToRgb(DrawingColor color) => new((byte)color.R, (byte)color.G, (byte)color.B);

    private static DrawingColor FromRgb(RgbColor color) => DrawingColor.FromArgb(color.R, color.G, color.B);

    private sealed record KeyCapCell(LightingZone Zone, Border Element);
}
