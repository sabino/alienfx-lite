using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AlienFxLite.Contracts;
using AlienFxLite.Hardware.Lighting;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;
using MediaBrushes = System.Windows.Media.Brushes;
using WinForms = System.Windows.Forms;
using WpfCursors = System.Windows.Input.Cursors;
using WpfButton = System.Windows.Controls.Button;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfMessageBox = System.Windows.MessageBox;
using WpfPoint = System.Windows.Point;

namespace AlienFxLite.UI;

public partial class MainWindow : Window
{
    private static readonly MediaColor Accent = MediaColor.FromRgb(94, 220, 255);
    private static readonly MediaColor AccentStrong = MediaColor.FromRgb(124, 255, 228);
    private static readonly MediaColor Danger = MediaColor.FromRgb(255, 122, 156);

    private readonly AlienFxLiteServiceClient _client = new();
    private readonly DesktopSettingsStore _desktopSettingsStore = new();
    private readonly LocalLightingStateStore _lightingStateStore = new();
    private readonly GitHubReleaseUpdateService _updateService = new();
    private readonly InstallerUpdateService _installerUpdateService = new();
    private readonly AlienFxLightingController _lightingController = new();
    private readonly AppLaunchOptions _launchOptions;
    private readonly WinForms.ColorDialog _colorDialog = new() { FullOpen = true };
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Dictionary<int, WpfButton> _zoneColorButtons = [];
    private readonly Dictionary<int, WpfButton> _zoneSecondaryButtons = [];
    private readonly Dictionary<int, Border> _zoneEditorCards = [];
    private readonly Dictionary<int, WpfComboBox> _zoneEffectCombos = [];
    private readonly Dictionary<int, Slider> _zoneSpeedSliders = [];
    private readonly Dictionary<int, TextBlock> _zoneSpeedTexts = [];
    private readonly Dictionary<int, Slider> _zoneBrightnessSliders = [];
    private readonly Dictionary<int, TextBlock> _zoneBrightnessTexts = [];
    private readonly Dictionary<int, WpfButton> _zoneMatchColorButtons = [];
    private readonly Dictionary<int, WpfButton> _zoneMatchEffectButtons = [];
    private readonly Dictionary<int, StackPanel> _zoneSecondaryPanels = [];
    private readonly Dictionary<int, StackPanel> _zonePalettePanels = [];
    private readonly Dictionary<int, WrapPanel> _zonePaletteWraps = [];
    private readonly List<KeyCapCell> _keyboardCells = [];
    private readonly Dictionary<int, ZoneLightingState> _draftZoneStates = [];
    private readonly WinForms.NotifyIcon _notifyIcon;

    private DesktopSettings _desktopSettings;
    private LocalLightingState _localLightingState;
    private LightingDeviceProfile? _lightingProfile;
    private Dictionary<string, LightingSnapshot> _lightingStatesByDeviceKey = new(StringComparer.OrdinalIgnoreCase);
    private DrawingColor _secondaryColor = DrawingColor.Black;
    private LightingSnapshot? _committedLightingSnapshot;
    private LightingSnapshot? _undoLightingSnapshot;
    private int? _activeZoneId;
    private bool _refreshing;
    private bool _loadingLightingState;
    private bool _loadingLightingProfiles;
    private bool _loadingDesktopSettings;
    private bool _lightingDirty;
    private bool _allowExit;
    private bool _checkingForUpdates;
    private bool _installingUpdate;
    private bool _allZonesSelected;
    private bool _startHiddenPending;
    private bool _trayTipShown;
    private string? _desktopSettingsError;
    private string? _localLightingError;
    private string? _updateError;
    private GitHubReleaseUpdateService.UpdateCheckResult? _lastUpdateResult;
    private double _contentZoomScale = 1d;

    public MainWindow()
        : this(new AppLaunchOptions(AppCommand.Ui, false, null, null))
    {
    }

    internal MainWindow(AppLaunchOptions launchOptions)
    {
        _launchOptions = launchOptions;
        _desktopSettings = _desktopSettingsStore.Load();
        _localLightingState = _lightingStateStore.Load();
        _lightingStatesByDeviceKey = _localLightingState.Snapshots
            .Where(static snapshot => !string.IsNullOrWhiteSpace(snapshot.DeviceKey))
            .ToDictionary(static snapshot => snapshot.DeviceKey!, static snapshot => snapshot, StringComparer.OrdinalIgnoreCase);

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

        _loadingLightingState = true;
        LightingProfileCombo.DisplayMemberPath = nameof(LightingDeviceProfile.DisplayName);
        UpdateEffectOptions(preserveSelection: false);
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
        UpdateColorButton(PrimaryColorButton, DrawingColor.White);
        UpdateColorButton(SecondaryColorButton, _secondaryColor);
        UpdateTrackLabels();
        UpdateEffectUi();
        UpdateLightingCapabilityUi();
        UpdateApplyButtonState();
        UpdateZoneActionButtons();
        UpdateDesktopStatus();
        UpdateUpdateUi();
        RebuildZoneEditorsAndDeck(null);
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

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        bool handled = e.Key switch
        {
            Key.Add or Key.OemPlus => TryAdjustZoom(0.1),
            Key.Subtract or Key.OemMinus => TryAdjustZoom(-0.1),
            Key.D0 or Key.NumPad0 => TrySetZoom(1d),
            _ => false,
        };

        if (handled)
        {
            e.Handled = true;
        }
    }

    private void ZoneEditorHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateZoneEditorColumns();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync(silent: false, preservePendingLighting: false).ConfigureAwait(true);
    }

    private bool TryAdjustZoom(double delta) => TrySetZoom(_contentZoomScale + delta);

    private bool TrySetZoom(double requestedScale)
    {
        double clamped = Math.Clamp(Math.Round(requestedScale, 2), 0.8d, 1.6d);
        if (Math.Abs(clamped - _contentZoomScale) < 0.01d)
        {
            return false;
        }

        _contentZoomScale = clamped;
        MainContentScaleTransform.ScaleX = clamped;
        MainContentScaleTransform.ScaleY = clamped;
        return true;
    }

    private async void ApplyLightingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lightingDirty)
        {
            await SaveLightingChangesAsync(showErrors: true, refreshBrokerStatus: true).ConfigureAwait(true);
            return;
        }

        LightingSnapshot? saved = _committedLightingSnapshot ?? (_lightingProfile is null ? null : ResolveLightingSnapshot(_lightingProfile));
        if (saved is null)
        {
            return;
        }

        await ApplyLightingSnapshotAsync(
                saved,
                showErrors: true,
                refreshBrokerStatus: true,
                persistState: false,
                setAsCommitted: false,
                successMessage: "Saved lighting restored to the keyboard.")
            .ConfigureAwait(true);
    }

    private async void FanAutoButton_Click(object sender, RoutedEventArgs e) =>
        await ApplyFanModeAsync(FanControlMode.Auto).ConfigureAwait(true);

    private async void FanMaxButton_Click(object sender, RoutedEventArgs e) =>
        await ApplyFanModeAsync(FanControlMode.Max).ConfigureAwait(true);

    private async void PrimaryColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ChoosePrimaryColorForSelection())
        {
            return;
        }

        await PreviewLightingChangesAsync(showErrors: true, refreshBrokerStatus: false).ConfigureAwait(true);
    }

    private async void SecondaryColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ChooseSecondaryColor())
        {
            return;
        }

        await PreviewLightingChangesAsync(showErrors: true, refreshBrokerStatus: false).ConfigureAwait(true);
    }

    private async void SyncAllButton_Click(object sender, RoutedEventArgs e)
    {
        SelectAllZones();
        await PreviewLightingChangesAsync(showErrors: false, refreshBrokerStatus: false).ConfigureAwait(true);
    }

    private async void UndoLightingButton_Click(object sender, RoutedEventArgs e)
    {
        LightingSnapshot? snapshot = _undoLightingSnapshot ?? _committedLightingSnapshot;
        if (snapshot is null)
        {
            return;
        }

        LoadLightingSnapshot(snapshot, setAsCommitted: true);
        await ApplyLightingSnapshotAsync(
                snapshot,
                showErrors: true,
                refreshBrokerStatus: false,
                persistState: false,
                setAsCommitted: false,
                successMessage: "Restored the last saved lighting state.")
            .ConfigureAwait(true);
    }

    private async void EffectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingLightingState)
        {
            UpdateEffectUi();
            return;
        }

        if (_lightingProfile is not null)
        {
            LightingEffect selectedEffect = LightingEffectCatalog.NormalizeEffect(_lightingProfile, (LightingEffect)(EffectCombo.SelectedItem ?? LightingEffect.Static));
            foreach (int zoneId in GetSelectedZoneIds())
            {
                ZoneLightingState existing = _draftZoneStates.TryGetValue(zoneId, out ZoneLightingState? state)
                    ? state
                    : new ZoneLightingState(zoneId, selectedEffect, new RgbColor(255, 255, 255), null, (int)SpeedSlider.Value, true);
                _draftZoneStates[zoneId] = existing with
                {
                    Effect = selectedEffect,
                    SecondaryColor = LightingEffectCatalog.SupportsSecondaryColor(selectedEffect)
                        ? existing.SecondaryColor ?? ToRgb(_secondaryColor)
                        : null,
                    Palette = LightingEffectCatalog.SupportsPalette(selectedEffect)
                        ? existing.Palette ?? LightingEffectCatalog.DefaultSpectrumPalette
                        : null,
                };
            }
        }

        UpdateEffectUi();
        MarkLightingDirty();

        await PreviewLightingChangesAsync(showErrors: true, refreshBrokerStatus: false).ConfigureAwait(true);
    }

    private void LightingProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingLightingProfiles)
        {
            return;
        }

        _lightingProfile = LightingProfileCombo.SelectedItem as LightingDeviceProfile;
        _lightingDirty = false;
        PersistActiveLightingProfileKey(_lightingProfile?.DeviceKey);
        _undoLightingSnapshot = null;
        RebuildZoneEditorsAndDeck(_lightingProfile);
        UpdateEffectOptions();
        LoadLightingSnapshot(ResolveLightingSnapshot(_lightingProfile));
    }

    internal void HandleExternalActivationRequest() =>
        Dispatcher.Invoke(RestoreFromTray);

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateTrackLabels();
        if (_loadingLightingState || _lightingProfile is null)
        {
            return;
        }

        foreach (int zoneId in GetSelectedZoneIds())
        {
            ZoneLightingState existing = _draftZoneStates.TryGetValue(zoneId, out ZoneLightingState? state)
                ? state
                : new ZoneLightingState(zoneId, LightingEffectCatalog.GetDefaultEffect(_lightingProfile), new RgbColor(255, 255, 255), null, (int)SpeedSlider.Value, EnabledCheck.IsChecked != false);
            _draftZoneStates[zoneId] = existing with { Speed = (int)SpeedSlider.Value };
        }

        MarkLightingDirty();
        Dispatcher.InvokeAsync(() => _ = PreviewLightingChangesAsync(showErrors: false, refreshBrokerStatus: false));
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateTrackLabels();
        MarkLightingDirty();
        Dispatcher.InvokeAsync(() => _ = PreviewLightingChangesAsync(showErrors: false, refreshBrokerStatus: false));
    }

    private void SettingChanged(object sender, RoutedEventArgs e) => MarkLightingDirty();

    private IReadOnlyList<int> GetSelectedZoneIds()
    {
        if (_lightingProfile is null)
        {
            return [];
        }

        if (_allZonesSelected)
        {
            return _lightingProfile.Zones.Select(static zone => zone.ZoneId).ToArray();
        }

        return _activeZoneId is int zoneId ? [zoneId] : [];
    }

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
        _ = InstallUpdateAsync();
    }

    private async Task InstallUpdateAsync()
    {
        if (_installingUpdate)
        {
            return;
        }

        GitHubReleaseUpdateService.GitHubReleaseInfo? release = _lastUpdateResult?.Release;
        if (release is null || string.IsNullOrWhiteSpace(release.InstallerUrl))
        {
            return;
        }

        MessageBoxResult confirmation = WpfMessageBox.Show(
            this,
            $"AlienFx Lite will download the {release.TagName} installer, launch setup, and close so the upgrade can continue.\n\nContinue?",
            "AlienFx Lite",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        _installingUpdate = true;
        _updateError = null;
        UpdateUpdateUi();

        try
        {
            await _installerUpdateService.DownloadAndLaunchAsync(release).ConfigureAwait(true);
            _allowExit = true;
            Close();
        }
        catch (HttpRequestException ex)
        {
            _updateError = $"Installer download failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            _updateError = ex.Message;
        }
        finally
        {
            _installingUpdate = false;
            if (IsLoaded)
            {
                UpdateUpdateUi();
            }
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
            StatusSnapshot? brokerStatus = null;
            Exception? brokerError = null;
            try
            {
                brokerStatus = await _client.GetStatusAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                brokerError = ex;
            }

            ApplyBrokerStatus(brokerStatus);
            RefreshLightingStatus(preservePendingLighting && _lightingDirty);

            if (!preservePendingLighting && ShouldMaintainLighting())
            {
                MaintainLighting();
            }

            UpdateDeviceStatusText(brokerStatus?.Devices.FanAvailable == true);

            if (brokerError is not null && !silent)
            {
                WpfMessageBox.Show(this, brokerError.Message, "AlienFx Lite", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void ApplyBrokerStatus(StatusSnapshot? status)
    {
        if (status is null)
        {
            ServiceStatusText.Text = "Broker unavailable";
            ServiceStatusText.Foreground = new SolidColorBrush(Danger);
            FanModeText.Text = "Mode: unavailable";
            FanRpmText.Text = "RPM: n/a";
            FanHintText.Text = "Fan control requires the broker service.";
            return;
        }

        ServiceStatusText.Text = "Broker connected";
        ServiceStatusText.Foreground = new SolidColorBrush(AccentStrong);
        FanModeText.Text = $"Mode: {status.Fan.Mode}";
        FanRpmText.Text = status.Fan.Rpm.Count > 0
            ? $"RPM: {string.Join(" / ", status.Fan.Rpm)}"
            : $"RPM: n/a ({status.Fan.Message})";
        FanHintText.Text = status.Fan.Message;
    }

    private void RefreshLightingStatus(bool preservePendingLighting)
    {
        bool lightingAvailable = _lightingController.Probe(out _localLightingError);
        IReadOnlyList<LightingDeviceProfile> profiles = lightingAvailable
            ? _lightingController.AvailableProfiles
            : _lightingProfile is null ? [] : [_lightingProfile];

        string? preferredProfileKey = preservePendingLighting && _lightingDirty
            ? _lightingProfile?.DeviceKey
            : _localLightingState.ActiveDeviceKey;

        UpdateLightingProfileSelector(profiles, preferredProfileKey);
        RebuildZoneEditorsAndDeck(_lightingProfile);
        UpdateEffectOptions();

        if (!preservePendingLighting)
        {
            LoadLightingSnapshot(ResolveLightingSnapshot(_lightingProfile));
        }

        UpdateTrackLabels();
        UpdateEffectUi();
        UpdateLightingCapabilityUi();
        UpdateApplyButtonState();
        RefreshKeyboardPreview();
    }

    private void UpdateDeviceStatusText(bool fanAvailable)
    {
        if (_lightingProfile is not null)
        {
            DeviceStatusText.Text = $"{_lightingProfile.DisplayName}  |  Lighting: online  |  Fans: {(fanAvailable ? "online" : "offline")}";
            return;
        }

        DeviceStatusText.Text = string.IsNullOrWhiteSpace(_localLightingError)
            ? $"Lighting: offline  |  Fans: {(fanAvailable ? "online" : "offline")}"
            : $"{_localLightingError}  |  Fans: {(fanAvailable ? "online" : "offline")}";
    }

    private void UpdateLightingProfileSelector(IReadOnlyList<LightingDeviceProfile> profiles, string? preferredProfileKey)
    {
        _loadingLightingProfiles = true;
        try
        {
            LightingProfileCombo.ItemsSource = profiles;

            LightingDeviceProfile? selected = !string.IsNullOrWhiteSpace(preferredProfileKey)
                ? profiles.FirstOrDefault(profile => string.Equals(profile.DeviceKey, preferredProfileKey, StringComparison.OrdinalIgnoreCase))
                : null;

            selected ??= _lightingProfile is null
                ? null
                : profiles.FirstOrDefault(profile => string.Equals(profile.DeviceKey, _lightingProfile.DeviceKey, StringComparison.OrdinalIgnoreCase));

            selected ??= profiles.FirstOrDefault();

            LightingProfileCombo.SelectedItem = selected;
            _lightingProfile = selected;
            PersistActiveLightingProfileKey(_lightingProfile?.DeviceKey);
        }
        finally
        {
            _loadingLightingProfiles = false;
        }
    }

    private void LoadLightingSnapshot(LightingSnapshot snapshot, bool setAsCommitted = true)
    {
        LightingSnapshot normalized = NormalizeSnapshot(snapshot);
        _loadingLightingState = true;
        try
        {
            BrightnessSlider.Value = normalized.Brightness;
            KeepAliveCheck.IsChecked = normalized.KeepAlive;
            EnabledCheck.IsChecked = normalized.Enabled;

            ZoneLightingState? reference = normalized.ZoneStates.OrderBy(static zone => zone.ZoneId).FirstOrDefault();
            if (reference is not null)
            {
                SpeedSlider.Value = reference.Speed;
                _secondaryColor = reference.SecondaryColor is null ? DrawingColor.Black : FromRgb(reference.SecondaryColor.Value);
                UpdateColorButton(SecondaryColorButton, _secondaryColor);
            }

            _draftZoneStates.Clear();
            foreach (ZoneLightingState zoneState in normalized.ZoneStates)
            {
                _draftZoneStates[zoneState.ZoneId] = zoneState;
            }

            _allZonesSelected = false;
            _activeZoneId = _lightingProfile?.Zones.Any(zone => zone.ZoneId == _activeZoneId) == true
                ? _activeZoneId
                : _lightingProfile?.Zones.FirstOrDefault()?.ZoneId;
        }
        finally
        {
            _loadingLightingState = false;
        }

        if (setAsCommitted)
        {
            _committedLightingSnapshot = normalized;
            _lightingDirty = false;
            _undoLightingSnapshot = null;
        }

        UpdateFocusedZoneControls();
        UpdateEffectUi();
        UpdateZoneEditorButtons();
        UpdateZoneActionButtons();
        RefreshKeyboardPreview();
        UpdateApplyButtonState();
    }

    private void UpdateEffectOptions(bool preserveSelection = true)
    {
        LightingEffect current = (LightingEffect)(EffectCombo.SelectedItem ?? LightingEffect.Static);
        IReadOnlyList<LightingEffect> effects = LightingEffectCatalog.GetSupportedEffects(_lightingProfile);
        LightingEffect selected = preserveSelection && effects.Contains(current)
            ? current
            : LightingEffectCatalog.GetDefaultEffect(_lightingProfile);

        bool wasLoadingLightingState = _loadingLightingState;
        _loadingLightingState = true;
        try
        {
            EffectCombo.ItemsSource = effects;
            EffectCombo.SelectedItem = selected;
        }
        finally
        {
            _loadingLightingState = wasLoadingLightingState;
        }

        UpdateFocusedZoneControls();
    }

    private ZoneLightingState? GetFocusedZoneState()
    {
        if (_activeZoneId is not int zoneId)
        {
            return null;
        }

        return _draftZoneStates.TryGetValue(zoneId, out ZoneLightingState? state)
            ? state
            : null;
    }

    private void UpdateFocusedZoneControls()
    {
        LightingEffect selected = LightingEffectCatalog.GetDefaultEffect(_lightingProfile);
        ZoneLightingState? activeState = GetFocusedZoneState();
        if (activeState is not null)
        {
            selected = LightingEffectCatalog.NormalizeEffect(_lightingProfile, activeState.Effect);
        }

        bool wasLoadingLightingState = _loadingLightingState;
        _loadingLightingState = true;
        try
        {
            FocusedZoneText.Text = _allZonesSelected
                ? "Selected section: all sections"
                : _activeZoneId is int zoneId
                    ? $"Selected section: {GetZoneName(zoneId)}"
                    : "Selected section: none";

            if (EffectCombo.Items.Count > 0)
            {
                EffectCombo.SelectedItem = selected;
            }

            if (activeState is not null)
            {
                SpeedSlider.Value = activeState.Speed;
            }

            DrawingColor focusedPrimary = activeState is not null
                ? FromRgb(activeState.PrimaryColor)
                : DrawingColor.White;
            DrawingColor focusedSecondary = activeState?.SecondaryColor is { } secondaryColor
                ? FromRgb(secondaryColor)
                : _secondaryColor;
            UpdateColorButton(PrimaryColorButton, focusedPrimary);
            UpdateColorButton(SecondaryColorButton, focusedSecondary);
        }
        finally
        {
            _loadingLightingState = wasLoadingLightingState;
        }
    }

    private LightingSnapshot ComposeWorkingSnapshot()
    {
        if (_lightingProfile is null)
        {
            return new LightingSnapshot(false, 100, false, null, []);
        }

        bool enabled = EnabledCheck.IsChecked != false;
        LightingEffect defaultEffect = LightingEffectCatalog.GetDefaultEffect(_lightingProfile);

        IReadOnlyList<ZoneLightingState> zones = _lightingProfile.Zones
            .Select(zone =>
            {
                ZoneLightingState draft = _draftZoneStates.TryGetValue(zone.ZoneId, out ZoneLightingState? state)
                    ? state
                    : new ZoneLightingState(zone.ZoneId, defaultEffect, new RgbColor(255, 255, 255), null, 50, enabled, 100, null);

                LightingEffect zoneEffect = LightingEffectCatalog.NormalizeEffect(_lightingProfile, draft.Effect);

                return draft with
                {
                    ZoneId = zone.ZoneId,
                    Effect = zoneEffect,
                    SecondaryColor = LightingEffectCatalog.SupportsSecondaryColor(zoneEffect)
                        ? draft.SecondaryColor ?? ToRgb(_secondaryColor)
                        : null,
                    Palette = LightingEffectCatalog.SupportsPalette(zoneEffect)
                        ? draft.Palette ?? LightingEffectCatalog.DefaultSpectrumPalette
                        : null,
                    Speed = Math.Clamp(draft.Speed, 0, 100),
                    Brightness = Math.Clamp(draft.Brightness, 0, 100),
                    Enabled = enabled,
                };
            })
            .OrderBy(static zone => zone.ZoneId)
            .ToArray();

        return NormalizeSnapshot(new LightingSnapshot(
            enabled,
            (int)BrightnessSlider.Value,
            KeepAliveCheck.IsChecked == true,
            _lightingProfile.DeviceKey,
            zones));
    }

    private LightingSnapshot ResolveLightingSnapshot(LightingDeviceProfile? profile)
    {
        if (profile is not null &&
            TryResolveStoredLightingSnapshot(profile, out LightingSnapshot? snapshot))
        {
            return snapshot;
        }

        return new LightingSnapshot(
            true,
            100,
            true,
            profile?.DeviceKey,
            profile?.Zones.Select(static zone => new ZoneLightingState(zone.ZoneId, LightingEffect.Static, new RgbColor(255, 255, 255), null, 50, true)).ToArray() ?? []);
    }

    private bool TryResolveStoredLightingSnapshot(LightingDeviceProfile profile, out LightingSnapshot snapshot)
    {
        if (_lightingStatesByDeviceKey.TryGetValue(profile.DeviceKey, out LightingSnapshot? exact))
        {
            snapshot = NormalizeSnapshot(exact with { DeviceKey = profile.DeviceKey });
            return true;
        }

        string templateKey = ExtractTemplateKey(profile.DeviceKey);
        LightingSnapshot? migrated = _lightingStatesByDeviceKey.Values.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate.DeviceKey) &&
            (string.Equals(ExtractTemplateKey(candidate.DeviceKey), templateKey, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(candidate.DeviceKey, BuildLegacyProfileKey(profile), StringComparison.OrdinalIgnoreCase)));

        if (migrated is not null)
        {
            snapshot = NormalizeSnapshot(migrated with { DeviceKey = profile.DeviceKey });
            return true;
        }

        snapshot = default!;
        return false;
    }

    private LightingSnapshot NormalizeSnapshot(LightingSnapshot snapshot)
    {
        if (_lightingProfile is null)
        {
            return snapshot;
        }

        Dictionary<int, ZoneLightingState> existing = snapshot.ZoneStates.ToDictionary(static zone => zone.ZoneId);
        IReadOnlyList<ZoneLightingState> zones = _lightingProfile.Zones
            .Select(zone => existing.TryGetValue(zone.ZoneId, out ZoneLightingState? state)
                ? state with { ZoneId = zone.ZoneId, Effect = LightingEffectCatalog.NormalizeEffect(_lightingProfile, state.Effect) }
                : new ZoneLightingState(zone.ZoneId, LightingEffectCatalog.GetDefaultEffect(_lightingProfile), new RgbColor(255, 255, 255), null, 50, true))
            .OrderBy(static zone => zone.ZoneId)
            .ToArray();

        return new LightingSnapshot(
            snapshot.Enabled,
            Math.Clamp(snapshot.Brightness, 0, 100),
            snapshot.KeepAlive,
            _lightingProfile.DeviceKey,
            zones);
    }

    private void SaveLocalLightingState(LightingSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.DeviceKey))
        {
            return;
        }

        _lightingStatesByDeviceKey[snapshot.DeviceKey] = snapshot;
        _localLightingState = new LocalLightingState(
            snapshot.DeviceKey,
            _lightingStatesByDeviceKey.Values
                .OrderBy(static state => state.DeviceKey, StringComparer.OrdinalIgnoreCase)
                .ToList());
        _lightingStateStore.Save(_localLightingState);
    }

    private async Task<bool> PreviewLightingChangesAsync(bool showErrors, bool refreshBrokerStatus)
    {
        if (_lightingProfile is null)
        {
            if (showErrors)
            {
                WpfMessageBox.Show(this, _localLightingError ?? "No supported AlienFX lighting surface is currently available.", "AlienFx Lite", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return false;
        }

        LightingSnapshot updated = ComposeWorkingSnapshot();
        return await ApplyLightingSnapshotAsync(
                updated,
                showErrors,
                refreshBrokerStatus,
                persistState: false,
                setAsCommitted: false,
                successMessage: "Live lighting preview applied. Press Save Lighting to persist it.")
            .ConfigureAwait(true);
    }

    private async Task<bool> SaveLightingChangesAsync(bool showErrors, bool refreshBrokerStatus)
    {
        if (_lightingProfile is null)
        {
            if (showErrors)
            {
                WpfMessageBox.Show(this, _localLightingError ?? "No supported AlienFX lighting surface is currently available.", "AlienFx Lite", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return false;
        }

        LightingSnapshot updated = ComposeWorkingSnapshot();
        return await ApplyLightingSnapshotAsync(
                updated,
                showErrors,
                refreshBrokerStatus,
                persistState: true,
                setAsCommitted: true,
                successMessage: "Lighting saved for restore, startup, and keepalive.")
            .ConfigureAwait(true);
    }

    private async Task<bool> ApplyLightingSnapshotAsync(
        LightingSnapshot snapshot,
        bool showErrors,
        bool refreshBrokerStatus,
        bool persistState,
        bool setAsCommitted,
        string successMessage)
    {
        try
        {
            LightingSnapshot normalized = NormalizeSnapshot(snapshot);

            if (!_lightingController.Apply(normalized, out string? applyError))
            {
                throw new InvalidOperationException(applyError ?? "Failed to apply keyboard lighting.");
            }

            if (persistState &&
                normalized.KeepAlive &&
                !_lightingController.PersistDefaultState(normalized, out string? persistError) &&
                !string.IsNullOrWhiteSpace(persistError))
            {
                PendingText.Text = persistError;
                PendingText.Foreground = new SolidColorBrush(Danger);
            }

            if (persistState)
            {
                SaveLocalLightingState(normalized);
                LoadLightingSnapshot(normalized, setAsCommitted);
            }
            _localLightingError = null;

            if (!persistState)
            {
                UpdateFocusedZoneControls();
                UpdateZoneEditorButtons();
                UpdateZoneActionButtons();
                RefreshKeyboardPreview();
                UpdateApplyButtonState();
            }

            UpdateApplyButtonState();

            if (refreshBrokerStatus)
            {
                StatusSnapshot? fanStatus = null;
                try
                {
                    fanStatus = await _client.GetStatusAsync().ConfigureAwait(true);
                }
                catch
                {
                }

                ApplyBrokerStatus(fanStatus);
                UpdateDeviceStatusText(fanStatus?.Devices.FanAvailable == true);
            }

            PendingText.Text = successMessage;
            PendingText.Foreground = new SolidColorBrush(AccentStrong);

            return true;
        }
        catch (Exception ex)
        {
            if (showErrors)
            {
                WpfMessageBox.Show(this, ex.Message, "AlienFx Lite", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                PendingText.Text = ex.Message;
                PendingText.Foreground = new SolidColorBrush(Danger);
            }

            return false;
        }
    }

    private void PersistActiveLightingProfileKey(string? deviceKey)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            return;
        }

        _localLightingState = _localLightingState with { ActiveDeviceKey = deviceKey };
        _lightingStateStore.Save(_localLightingState);
    }

    private bool ShouldMaintainLighting()
    {
        if (_lightingDirty || _lightingProfile is null)
        {
            return false;
        }

        if (!TryResolveStoredLightingSnapshot(_lightingProfile, out LightingSnapshot snapshot))
        {
            return false;
        }

        return snapshot.Enabled && snapshot.KeepAlive;
    }

    private void MaintainLighting()
    {
        if (_lightingProfile is null)
        {
            return;
        }

        if (!TryResolveStoredLightingSnapshot(_lightingProfile, out LightingSnapshot snapshot))
        {
            return;
        }

        if (_lightingController.Maintain(snapshot, out string? error))
        {
            _localLightingError = null;
            return;
        }

        _localLightingError = error;
    }

    private static string BuildLegacyProfileKey(LightingDeviceProfile profile) =>
        $"{profile.VendorId:X4}:{profile.ProductId:X4}:{profile.SurfaceName}";

    private static string ExtractTemplateKey(string? deviceKey)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            return string.Empty;
        }

        int separator = deviceKey.IndexOf('|');
        return separator >= 0 && separator + 1 < deviceKey.Length
            ? deviceKey[(separator + 1)..]
            : deviceKey;
    }

    private void RebuildZoneEditorsAndDeck(LightingDeviceProfile? profile)
    {
        _lightingProfile = profile;
        ZoneEditorPanel.Children.Clear();
        _zoneColorButtons.Clear();
        _zoneSecondaryButtons.Clear();
        _zoneEditorCards.Clear();
        _zoneEffectCombos.Clear();
        _zoneSpeedSliders.Clear();
        _zoneSpeedTexts.Clear();
        _zoneBrightnessSliders.Clear();
        _zoneBrightnessTexts.Clear();
        _zoneMatchColorButtons.Clear();
        _zoneMatchEffectButtons.Clear();
        _zoneSecondaryPanels.Clear();
        _zonePalettePanels.Clear();
        _zonePaletteWraps.Clear();

        if (_lightingProfile is not null && !_lightingProfile.Zones.Any(zone => zone.ZoneId == _activeZoneId))
        {
            _activeZoneId = _lightingProfile.Zones.FirstOrDefault()?.ZoneId;
        }

        foreach (LightingZoneDefinition zone in profile?.Zones ?? [])
        {
            Border card = new()
            {
                Style = (Style)FindResource("ZoneEditorCardStyle"),
                Tag = zone.ZoneId,
                Cursor = WpfCursors.Hand,
            };
            card.MouseLeftButtonUp += ZoneEditorCard_MouseLeftButtonUp;

            Grid cardGrid = new();
            for (int index = 0; index < 8; index++)
            {
                cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            TextBlock title = new()
            {
                Text = zone.Name,
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Display"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
            };

            WpfComboBox effectCombo = new()
            {
                Tag = zone.ZoneId,
                Height = 34,
                Margin = new Thickness(0, 8, 0, 10),
                Style = (Style)FindResource("CompactGlassComboBoxStyle"),
                ItemsSource = LightingEffectCatalog.GetSupportedEffects(profile),
            };
            effectCombo.SelectionChanged += ZoneEffectCombo_SelectionChanged;

            WpfButton colorButton = new()
            {
                Tag = zone.ZoneId,
                Height = 40,
                Style = (Style)FindResource("GlassButtonStyle"),
            };
            colorButton.Click += ZoneColorButton_Click;

            StackPanel secondaryPanel = new()
            {
                Margin = new Thickness(0, 10, 0, 0),
            };
            secondaryPanel.Children.Add(new TextBlock
            {
                Text = "Secondary",
                Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
                FontSize = 10,
                Margin = new Thickness(0, 0, 0, 4),
            });
            WpfButton secondaryButton = new()
            {
                Tag = zone.ZoneId,
                Height = 34,
                Style = (Style)FindResource("GlassButtonStyle"),
            };
            secondaryButton.Click += ZoneSecondaryButton_Click;
            secondaryPanel.Children.Add(secondaryButton);

            StackPanel palettePanel = new()
            {
                Margin = new Thickness(0, 10, 0, 0),
            };
            palettePanel.Children.Add(new TextBlock
            {
                Text = "Spectrum Colors",
                Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
                FontSize = 10,
                Margin = new Thickness(0, 0, 0, 4),
            });
            WrapPanel paletteWrap = new()
            {
                Margin = new Thickness(0, 0, 0, 0),
            };
            palettePanel.Children.Add(paletteWrap);

            Grid speedGrid = new()
            {
                Margin = new Thickness(0, 10, 0, 0),
            };
            speedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            speedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            speedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            speedGrid.Children.Add(new TextBlock
            {
                Text = "Speed",
                Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });
            Slider speedSlider = new()
            {
                Tag = zone.ZoneId,
                Minimum = 0,
                Maximum = 100,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("GlassSliderStyle"),
            };
            speedSlider.ValueChanged += ZoneSpeedSlider_ValueChanged;
            Grid.SetColumn(speedSlider, 1);
            speedGrid.Children.Add(speedSlider);
            TextBlock speedValue = new()
            {
                Foreground = new SolidColorBrush(Accent),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Display"),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(speedValue, 2);
            speedGrid.Children.Add(speedValue);

            Grid brightnessGrid = new()
            {
                Margin = new Thickness(0, 8, 0, 0),
            };
            brightnessGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            brightnessGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            brightnessGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            brightnessGrid.Children.Add(new TextBlock
            {
                Text = "Zone Brightness",
                Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });
            Slider brightnessSlider = new()
            {
                Tag = zone.ZoneId,
                Minimum = 0,
                Maximum = 100,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("GlassSliderStyle"),
            };
            brightnessSlider.ValueChanged += ZoneBrightnessSlider_ValueChanged;
            Grid.SetColumn(brightnessSlider, 1);
            brightnessGrid.Children.Add(brightnessSlider);
            TextBlock brightnessValue = new()
            {
                Foreground = new SolidColorBrush(Accent),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Display"),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(brightnessValue, 2);
            brightnessGrid.Children.Add(brightnessValue);

            Grid actionsGrid = new()
            {
                Margin = new Thickness(0, 10, 0, 0),
            };
            actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            WpfButton matchColorsButton = new()
            {
                Tag = zone.ZoneId,
                Height = 32,
                Margin = new Thickness(0, 0, 6, 0),
                Style = (Style)FindResource("GlassButtonStyle"),
                FontSize = 11,
                Content = "Match Colors",
            };
            matchColorsButton.Click += ZoneMatchColorsButton_Click;
            actionsGrid.Children.Add(matchColorsButton);
            WpfButton matchEffectButton = new()
            {
                Tag = zone.ZoneId,
                Height = 32,
                Margin = new Thickness(6, 0, 0, 0),
                Style = (Style)FindResource("GlassButtonStyle"),
                FontSize = 11,
                Content = "Match Effect",
            };
            matchEffectButton.Click += ZoneMatchEffectButton_Click;
            Grid.SetColumn(matchEffectButton, 1);
            actionsGrid.Children.Add(matchEffectButton);

            cardGrid.Children.Add(title);
            Grid.SetRow(effectCombo, 1);
            cardGrid.Children.Add(effectCombo);
            Grid.SetRow(colorButton, 2);
            cardGrid.Children.Add(colorButton);
            Grid.SetRow(secondaryPanel, 3);
            cardGrid.Children.Add(secondaryPanel);
            Grid.SetRow(palettePanel, 4);
            cardGrid.Children.Add(palettePanel);
            Grid.SetRow(speedGrid, 5);
            cardGrid.Children.Add(speedGrid);
            Grid.SetRow(brightnessGrid, 6);
            cardGrid.Children.Add(brightnessGrid);
            Grid.SetRow(actionsGrid, 7);
            cardGrid.Children.Add(actionsGrid);
            card.Child = cardGrid;

            ZoneEditorPanel.Children.Add(card);
            _zoneColorButtons[zone.ZoneId] = colorButton;
            _zoneSecondaryButtons[zone.ZoneId] = secondaryButton;
            _zoneEditorCards[zone.ZoneId] = card;
            _zoneEffectCombos[zone.ZoneId] = effectCombo;
            _zoneSpeedSliders[zone.ZoneId] = speedSlider;
            _zoneSpeedTexts[zone.ZoneId] = speedValue;
            _zoneBrightnessSliders[zone.ZoneId] = brightnessSlider;
            _zoneBrightnessTexts[zone.ZoneId] = brightnessValue;
            _zoneMatchColorButtons[zone.ZoneId] = matchColorsButton;
            _zoneMatchEffectButtons[zone.ZoneId] = matchEffectButton;
            _zoneSecondaryPanels[zone.ZoneId] = secondaryPanel;
            _zonePalettePanels[zone.ZoneId] = palettePanel;
            _zonePaletteWraps[zone.ZoneId] = paletteWrap;
        }

        UpdateZoneEditorColumns();

        BuildKeyboardDeck(profile);
        UpdateFocusedZoneControls();
        UpdateZoneEditorButtons();
        UpdateZoneActionButtons();
    }

    private void BuildKeyboardDeck(LightingDeviceProfile? profile)
    {
        KeyboardDeckDescriptionText.Text = profile?.PreviewGrid is not null
            ? $"This deck mirrors the mapped '{profile.PreviewGrid.Name}' surface. Click any lit region or use the matching zone card below."
            : "This deck previews the currently detected AlienFX zones. Click any lit region or use the matching zone card below.";

        KeyboardMatrixPanel.Children.Clear();
        _keyboardCells.Clear();
        IReadOnlyList<int?> layoutCells = BuildDeckCells(profile, out int columns, out int rows);
        KeyboardMatrixPanel.Columns = Math.Max(columns, 1);
        KeyboardMatrixPanel.Rows = Math.Max(rows, 1);

        foreach (int? zoneId in layoutCells)
        {
            Border keyCap = new()
            {
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                MinHeight = 22,
            };

            if (zoneId is null)
            {
                keyCap.Opacity = 0;
                keyCap.IsHitTestVisible = false;
                KeyboardMatrixPanel.Children.Add(keyCap);
                continue;
            }

            keyCap.Tag = zoneId.Value;
            keyCap.Cursor = WpfCursors.Hand;
            keyCap.MouseLeftButtonUp += KeyboardCell_MouseLeftButtonUp;
            KeyboardMatrixPanel.Children.Add(keyCap);
            _keyboardCells.Add(new KeyCapCell(zoneId.Value, keyCap));
        }
    }

    private void KeyboardCell_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: int zoneId })
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            SelectAllZones();
            e.Handled = true;
            return;
        }

        SetActiveZone(zoneId);
        e.Handled = true;
    }

    private void ZoneEditorCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: int zoneId })
        {
            if (e.ClickCount >= 2)
            {
                SelectAllZones();
                e.Handled = true;
                return;
            }

            SetActiveZone(zoneId);
            e.Handled = true;
        }
    }

    private void UpdateZoneEditorColumns()
    {
        int zoneCount = _lightingProfile?.Zones.Count ?? 0;
        if (zoneCount <= 0)
        {
            ZoneEditorPanel.Columns = 1;
            return;
        }

        double availableWidth = ZoneEditorHost.ActualWidth;
        if (availableWidth <= 0)
        {
            ZoneEditorPanel.Columns = Math.Min(zoneCount, 4);
            return;
        }

        const double targetCardWidth = 180d;
        int computedColumns = Math.Max(1, (int)Math.Floor((availableWidth + 10d) / targetCardWidth));
        ZoneEditorPanel.Columns = Math.Clamp(computedColumns, 1, Math.Min(zoneCount, 4));
    }

    private async void ZoneColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: int zoneId })
        {
            return;
        }

        if (!ChooseZoneColor(zoneId))
        {
            return;
        }

        await PreviewLightingChangesAsync(showErrors: true, refreshBrokerStatus: false).ConfigureAwait(true);
    }

    private async void ZoneSecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: int zoneId })
        {
            return;
        }

        if (!ChooseZoneSecondaryColor(zoneId))
        {
            return;
        }

        await PreviewLightingChangesAsync(showErrors: true, refreshBrokerStatus: false).ConfigureAwait(true);
    }

    private async void ZoneEffectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingLightingState || sender is not WpfComboBox { Tag: int zoneId, SelectedItem: LightingEffect selectedEffect })
        {
            return;
        }

        SetActiveZone(zoneId);
        LightingEffect normalizedEffect = LightingEffectCatalog.NormalizeEffect(_lightingProfile, selectedEffect);
        ZoneLightingState existing = _draftZoneStates.TryGetValue(zoneId, out ZoneLightingState? state)
            ? state
            : new ZoneLightingState(zoneId, normalizedEffect, ToRgb(DrawingColor.White), null, (int)SpeedSlider.Value, EnabledCheck.IsChecked != false);

        _draftZoneStates[zoneId] = existing with
        {
            Effect = normalizedEffect,
            SecondaryColor = normalizedEffect == LightingEffect.Morph
                ? existing.SecondaryColor ?? ToRgb(_secondaryColor)
                : existing.SecondaryColor,
        };

        MarkLightingDirty();
        await PreviewLightingChangesAsync(showErrors: true, refreshBrokerStatus: false).ConfigureAwait(true);
    }

    private async void ZoneSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingLightingState || sender is not Slider { Tag: int zoneId } slider)
        {
            return;
        }

        SetActiveZone(zoneId);
        ZoneLightingState existing = _draftZoneStates.TryGetValue(zoneId, out ZoneLightingState? state)
            ? state
            : new ZoneLightingState(zoneId, LightingEffectCatalog.GetDefaultEffect(_lightingProfile), new RgbColor(255, 255, 255), null, (int)slider.Value, EnabledCheck.IsChecked != false);
        _draftZoneStates[zoneId] = existing with { Speed = (int)slider.Value };
        MarkLightingDirty();
        await PreviewLightingChangesAsync(showErrors: false, refreshBrokerStatus: false).ConfigureAwait(true);
    }

    private async void ZoneBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingLightingState || sender is not Slider { Tag: int zoneId } slider)
        {
            return;
        }

        SetActiveZone(zoneId);
        ZoneLightingState existing = _draftZoneStates.TryGetValue(zoneId, out ZoneLightingState? state)
            ? state
            : new ZoneLightingState(zoneId, LightingEffectCatalog.GetDefaultEffect(_lightingProfile), new RgbColor(255, 255, 255), null, 50, EnabledCheck.IsChecked != false, (int)slider.Value);
        _draftZoneStates[zoneId] = existing with { Brightness = (int)slider.Value };
        MarkLightingDirty();
        await PreviewLightingChangesAsync(showErrors: false, refreshBrokerStatus: false).ConfigureAwait(true);
    }

    private async void ZoneMatchColorsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: int zoneId } || !_draftZoneStates.TryGetValue(zoneId, out ZoneLightingState? source))
        {
            return;
        }

        foreach (int targetZoneId in _draftZoneStates.Keys.ToArray())
        {
            ZoneLightingState existing = _draftZoneStates[targetZoneId];
            _draftZoneStates[targetZoneId] = existing with
            {
                PrimaryColor = source.PrimaryColor,
                SecondaryColor = source.SecondaryColor,
                Palette = source.Palette,
                Brightness = source.Brightness,
            };
        }

        SetActiveZone(zoneId);
        MarkLightingDirty();
        await PreviewLightingChangesAsync(showErrors: true, refreshBrokerStatus: false).ConfigureAwait(true);
    }

    private async void ZoneMatchEffectButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: int zoneId } || !_draftZoneStates.TryGetValue(zoneId, out ZoneLightingState? source))
        {
            return;
        }

        foreach (int targetZoneId in _draftZoneStates.Keys.ToArray())
        {
            ZoneLightingState existing = _draftZoneStates[targetZoneId];
            _draftZoneStates[targetZoneId] = existing with
            {
                Effect = source.Effect,
                Speed = source.Speed,
            };
        }

        SetActiveZone(zoneId);
        MarkLightingDirty();
        await PreviewLightingChangesAsync(showErrors: true, refreshBrokerStatus: false).ConfigureAwait(true);
    }

    private async void ZoneSpectrumColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: Tuple<int, int> tag })
        {
            return;
        }

        if (!_draftZoneStates.TryGetValue(tag.Item1, out ZoneLightingState? state))
        {
            return;
        }

        List<RgbColor> palette = (state.Palette is { Count: > 0 } ? state.Palette : LightingEffectCatalog.DefaultSpectrumPalette)
            .Take(7)
            .ToList();
        if (tag.Item2 < 0 || tag.Item2 >= palette.Count)
        {
            return;
        }

        _colorDialog.Color = FromRgb(palette[tag.Item2]);
        if (_colorDialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        palette[tag.Item2] = ToRgb(_colorDialog.Color);
        _draftZoneStates[tag.Item1] = state with { Palette = palette };
        SetActiveZone(tag.Item1);
        MarkLightingDirty();
        await PreviewLightingChangesAsync(showErrors: true, refreshBrokerStatus: false).ConfigureAwait(true);
    }

    private async void ZoneSpectrumAddColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: int zoneId } || !_draftZoneStates.TryGetValue(zoneId, out ZoneLightingState? state))
        {
            return;
        }

        List<RgbColor> palette = (state.Palette is { Count: > 0 } ? state.Palette : LightingEffectCatalog.DefaultSpectrumPalette)
            .Take(7)
            .ToList();
        if (palette.Count >= 7)
        {
            return;
        }

        _colorDialog.Color = DrawingColor.White;
        if (_colorDialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        palette.Add(ToRgb(_colorDialog.Color));
        _draftZoneStates[zoneId] = state with { Palette = palette };
        SetActiveZone(zoneId);
        MarkLightingDirty();
        await PreviewLightingChangesAsync(showErrors: true, refreshBrokerStatus: false).ConfigureAwait(true);
    }

    private async void ZoneSpectrumRemoveColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: int zoneId } || !_draftZoneStates.TryGetValue(zoneId, out ZoneLightingState? state))
        {
            return;
        }

        List<RgbColor> palette = (state.Palette is { Count: > 0 } ? state.Palette : LightingEffectCatalog.DefaultSpectrumPalette)
            .Take(7)
            .ToList();
        if (palette.Count <= 2)
        {
            return;
        }

        palette.RemoveAt(palette.Count - 1);
        _draftZoneStates[zoneId] = state with { Palette = palette };
        SetActiveZone(zoneId);
        MarkLightingDirty();
        await PreviewLightingChangesAsync(showErrors: true, refreshBrokerStatus: false).ConfigureAwait(true);
    }

    private bool ChooseZoneColor(int zoneId)
    {
        DrawingColor current = _draftZoneStates.TryGetValue(zoneId, out ZoneLightingState? state)
            ? FromRgb(state.PrimaryColor)
            : DrawingColor.White;
        _colorDialog.Color = current;
        if (_colorDialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return false;
        }

        SetActiveZone(zoneId);
        ZoneLightingState next = _draftZoneStates.TryGetValue(zoneId, out ZoneLightingState? existing)
            ? existing with { PrimaryColor = ToRgb(_colorDialog.Color) }
            : new ZoneLightingState(zoneId, LightingEffectCatalog.GetDefaultEffect(_lightingProfile), ToRgb(_colorDialog.Color), null, 50, true);
        _draftZoneStates[zoneId] = next;
        MarkLightingDirty();
        return true;
    }

    private bool ChooseZoneSecondaryColor(int zoneId)
    {
        DrawingColor current = _draftZoneStates.TryGetValue(zoneId, out ZoneLightingState? state) && state.SecondaryColor is { } secondary
            ? FromRgb(secondary)
            : DrawingColor.Black;
        _colorDialog.Color = current;
        if (_colorDialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return false;
        }

        SetActiveZone(zoneId);
        ZoneLightingState existing = _draftZoneStates.TryGetValue(zoneId, out ZoneLightingState? draft)
            ? draft
            : new ZoneLightingState(zoneId, LightingEffect.Morph, ToRgb(DrawingColor.White), ToRgb(_colorDialog.Color), 50, true);
        _draftZoneStates[zoneId] = existing with
        {
            Effect = existing.Effect == LightingEffect.Static ? LightingEffect.Morph : existing.Effect,
            SecondaryColor = ToRgb(_colorDialog.Color),
        };

        MarkLightingDirty();
        return true;
    }

    private bool ChoosePrimaryColorForSelection()
    {
        IReadOnlyList<int> selectedZoneIds = GetSelectedZoneIds();
        if (selectedZoneIds.Count == 0)
        {
            return false;
        }

        int referenceZoneId = _activeZoneId is int activeZoneId && selectedZoneIds.Contains(activeZoneId)
            ? activeZoneId
            : selectedZoneIds[0];
        DrawingColor current = _draftZoneStates.TryGetValue(referenceZoneId, out ZoneLightingState? state)
            ? FromRgb(state.PrimaryColor)
            : DrawingColor.White;

        _colorDialog.Color = current;
        if (_colorDialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return false;
        }

        foreach (int zoneId in selectedZoneIds)
        {
            ZoneLightingState existing = _draftZoneStates.TryGetValue(zoneId, out ZoneLightingState? draft)
                ? draft
                : new ZoneLightingState(zoneId, LightingEffectCatalog.GetDefaultEffect(_lightingProfile), ToRgb(_colorDialog.Color), null, 50, true);
            _draftZoneStates[zoneId] = existing with { PrimaryColor = ToRgb(_colorDialog.Color) };
        }

        UpdateColorButton(PrimaryColorButton, _colorDialog.Color);
        MarkLightingDirty();
        return true;
    }

    private bool ChooseSecondaryColor()
    {
        IReadOnlyList<int> selectedZoneIds = GetSelectedZoneIds();
        if (selectedZoneIds.Count == 0)
        {
            return false;
        }

        int referenceZoneId = _activeZoneId is int activeZoneId && selectedZoneIds.Contains(activeZoneId)
            ? activeZoneId
            : selectedZoneIds[0];
        ZoneLightingState? focusedState = _draftZoneStates.TryGetValue(referenceZoneId, out ZoneLightingState? state)
            ? state
            : null;
        DrawingColor current = focusedState?.SecondaryColor is { } secondaryColor
            ? FromRgb(secondaryColor)
            : _secondaryColor;

        _colorDialog.Color = current;
        if (_colorDialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return false;
        }

        _secondaryColor = _colorDialog.Color;
        foreach (int zoneId in selectedZoneIds)
        {
            ZoneLightingState existing = _draftZoneStates.TryGetValue(zoneId, out ZoneLightingState? draftState)
                ? draftState
                : new ZoneLightingState(zoneId, LightingEffectCatalog.GetDefaultEffect(_lightingProfile), ToRgb(DrawingColor.White), ToRgb(_secondaryColor), 50, true);
            _draftZoneStates[zoneId] = existing with
            {
                SecondaryColor = ToRgb(_secondaryColor),
                Effect = existing.Effect == LightingEffect.Static ? LightingEffect.Morph : existing.Effect,
            };
        }

        UpdateColorButton(SecondaryColorButton, _secondaryColor);
        MarkLightingDirty();
        return true;
    }

    private void SetActiveZone(int zoneId)
    {
        _allZonesSelected = false;
        _activeZoneId = zoneId;
        UpdateFocusedZoneControls();
        UpdateEffectUi();
        UpdateZoneActionButtons();
        UpdateZoneEditorButtons();
        RefreshKeyboardPreview();
    }

    private void SelectAllZones()
    {
        if (_lightingProfile is null || _lightingProfile.Zones.Count == 0)
        {
            return;
        }

        _allZonesSelected = true;
        _activeZoneId ??= _lightingProfile.Zones.First().ZoneId;
        UpdateFocusedZoneControls();
        UpdateEffectUi();
        UpdateZoneActionButtons();
        UpdateZoneEditorButtons();
        RefreshKeyboardPreview();
    }

    private void MarkLightingDirty()
    {
        if (_loadingLightingState)
        {
            return;
        }

        if (!_lightingDirty && _committedLightingSnapshot is not null)
        {
            _undoLightingSnapshot = _committedLightingSnapshot;
        }

        _lightingDirty = true;
        UpdateApplyButtonState();
        UpdateZoneEditorButtons();
        UpdateZoneActionButtons();
        RefreshKeyboardPreview();
    }

    private void UpdateApplyButtonState()
    {
        if (_lightingProfile is null)
        {
            ApplyButtonTitleText.Text = "Save Lighting";
            ApplyButtonHintText.Text = "A local lighting surface must be detected before keyboard changes can be sent.";
            ApplyLightingButton.IsEnabled = false;
            PendingText.Text = string.IsNullOrWhiteSpace(_localLightingError)
                ? "No lighting surface is currently available."
                : _localLightingError;
            PendingText.Foreground = new SolidColorBrush(Danger);
            ApplyLightingButton.Background = new SolidColorBrush(MediaColor.FromRgb(31, 51, 70));
            ApplyLightingButton.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(83, 108, 141));
            return;
        }

        ApplyLightingButton.IsEnabled = true;
        ApplyButtonTitleText.Text = _lightingDirty ? "Save Lighting" : "Restore Saved Lighting";
        ApplyButtonHintText.Text = _lightingDirty
            ? "Live changes are already on the keyboard. Save to persist them for restore, startup, and keepalive."
            : "Push the last saved lighting state back onto the keyboard if firmware drifted.";

        PendingText.Text = _lightingDirty
            ? "Unsaved live changes. Save to persist them, or use Undo to go back to the last saved state."
            : "Keyboard deck matches the saved local state.";
        PendingText.Foreground = new SolidColorBrush(_lightingDirty ? AccentStrong : Colors.White);
        ApplyLightingButton.Background = _lightingDirty
            ? new LinearGradientBrush(
                MediaColor.FromRgb(84, 235, 255),
                MediaColor.FromRgb(27, 162, 207),
                new WpfPoint(0, 0),
                new WpfPoint(1, 1))
            : new LinearGradientBrush(
                MediaColor.FromRgb(34, 74, 106),
                MediaColor.FromRgb(19, 46, 71),
                new WpfPoint(0, 0),
                new WpfPoint(1, 1));
        ApplyLightingButton.BorderBrush = new SolidColorBrush(_lightingDirty
            ? MediaColor.FromRgb(236, 255, 255)
            : MediaColor.FromRgb(109, 162, 201));
    }

    private void UpdateEffectUi()
    {
        IReadOnlyCollection<ZoneLightingState> states = _draftZoneStates.Values;
        LightingEffect focusedEffect = GetFocusedZoneState()?.Effect ?? LightingEffectCatalog.GetDefaultEffect(_lightingProfile);
        bool focusedMorph = LightingEffectCatalog.SupportsSecondaryColor(focusedEffect);
        bool anyAnimated = states.Any(state => LightingEffectCatalog.IsAnimated(state.Effect));
        bool usesPrimaryColor = LightingEffectCatalog.UsesPrimaryColor(focusedEffect);
        PrimaryPanel.Visibility = usesPrimaryColor ? Visibility.Visible : Visibility.Collapsed;
        SecondaryPanel.Visibility = focusedMorph ? Visibility.Visible : Visibility.Collapsed;
        SecondaryColorButton.Visibility = focusedMorph ? Visibility.Visible : Visibility.Collapsed;
        SpeedSlider.IsEnabled = anyAnimated;
        SpeedValueText.IsEnabled = anyAnimated;
        UpdateZoneEditorButtons();
        UpdateZoneActionButtons();
    }

    private void UpdateLightingCapabilityUi()
    {
        bool brightnessSupported = _lightingProfile?.SupportsBrightness != false;
        BrightnessSlider.IsEnabled = brightnessSupported;
        BrightnessValueText.IsEnabled = brightnessSupported;
        EffectCombo.IsEnabled = EffectCombo.Items.Count > 0;

        if (!brightnessSupported)
        {
            BrightnessSlider.Value = 100;
        }
    }

    private void UpdateTrackLabels()
    {
        SpeedValueText.Text = $"{(int)SpeedSlider.Value}%";
        BrightnessValueText.Text = $"{(int)BrightnessSlider.Value}%";
    }

    private void RefreshKeyboardPreview()
    {
        if (_lightingProfile is null || _keyboardCells.Count == 0)
        {
            LightingHintText.Text = string.IsNullOrWhiteSpace(_localLightingError)
                ? "No local AlienFX lighting surface is currently available."
                : _localLightingError;
            return;
        }

        LightingSnapshot preview = ComposeWorkingSnapshot();
        Dictionary<int, ZoneLightingState> zones = preview.ZoneStates.ToDictionary(static zone => zone.ZoneId);

        foreach (KeyCapCell cell in _keyboardCells)
        {
            if (!zones.TryGetValue(cell.ZoneId, out ZoneLightingState? zoneState))
            {
                cell.Element.Background = BuildKeyBrush(false, false, new ZoneLightingState(cell.ZoneId, LightingEffect.Static, new RgbColor(255, 255, 255), null, 50, false, 100, null));
                cell.Element.BorderBrush = new SolidColorBrush(MediaColor.FromArgb(92, 76, 97, 125));
                cell.Element.Opacity = 0.9;
                continue;
            }

            bool zoneEnabled = preview.Enabled && zoneState.Enabled;
            MediaColor primary = ToMediaColor(ScaleColor(zoneState.PrimaryColor, zoneState.Brightness));
            bool active = _allZonesSelected || _activeZoneId == zoneState.ZoneId;
            cell.Element.Background = BuildKeyBrush(true, zoneEnabled, zoneState);
            cell.Element.BorderBrush = new SolidColorBrush(active
                ? WithAlpha(Lighten(primary, zoneEnabled ? 0.30 : 0.14), 244)
                : WithAlpha(Lighten(primary, zoneEnabled ? 0.14 : 0.04), zoneEnabled ? (byte)170 : (byte)110));
            cell.Element.Opacity = zoneEnabled ? 1 : 0.78;
        }

        string activeZone = _allZonesSelected
            ? "All sections"
            : _activeZoneId is int zoneId ? GetZoneName(zoneId) : "No focused zone";
        int distinctColorCount = preview.ZoneStates
            .Select(static zone => zone.PrimaryColor)
            .Distinct()
            .Count();

        LightingEffect focusedEffect = GetFocusedZoneState()?.Effect ?? LightingEffectCatalog.GetDefaultEffect(_lightingProfile);
        bool usesWholeSurface = preview.ZoneStates.Any(zone => LightingEffectCatalog.RequiresWholeDeviceSelection(_lightingProfile, zone.Effect));
        string surfaceHint = usesWholeSurface
            ? "  |  API v5 animation applies to the whole surface"
            : string.Empty;

        LightingHintText.Text =
            $"Selection: {activeZone}  |  Effect: {focusedEffect}  |  Colors in deck: {distinctColorCount}  |  Deck Brightness: {(int)BrightnessSlider.Value}%  |  KeepAlive: {(KeepAliveCheck.IsChecked == true ? "on" : "off")}{surfaceHint}";
    }

    private string GetZoneName(int zoneId) =>
        _lightingProfile?.Zones.FirstOrDefault(zone => zone.ZoneId == zoneId)?.Name
        ?? $"Zone {zoneId}";

    private void UpdateZoneEditorButtons()
    {
        foreach ((int zoneId, WpfButton button) in _zoneColorButtons)
        {
            ZoneLightingState state = _draftZoneStates.TryGetValue(zoneId, out ZoneLightingState? existing)
                ? existing
                : new ZoneLightingState(zoneId, LightingEffectCatalog.GetDefaultEffect(_lightingProfile), new RgbColor(255, 255, 255), null, 50, true, 100, null);
            DrawingColor color = FromRgb(state.PrimaryColor);
            button.Visibility = LightingEffectCatalog.UsesPrimaryColor(state.Effect)
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (button.Visibility == Visibility.Visible)
            {
                UpdateColorButton(button, color);
            }

            if (_zoneSecondaryButtons.TryGetValue(zoneId, out WpfButton? secondaryButton))
            {
                DrawingColor secondaryColor = state.SecondaryColor is { } secondary
                    ? FromRgb(secondary)
                    : DrawingColor.Black;
                UpdateColorButton(secondaryButton, secondaryColor);
            }

            if (_zoneSecondaryPanels.TryGetValue(zoneId, out StackPanel? secondaryPanel))
            {
                secondaryPanel.Visibility = LightingEffectCatalog.SupportsSecondaryColor(state.Effect)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (_zonePalettePanels.TryGetValue(zoneId, out StackPanel? palettePanel))
            {
                bool showPalette = LightingEffectCatalog.SupportsPalette(state.Effect);
                palettePanel.Visibility = showPalette ? Visibility.Visible : Visibility.Collapsed;
                if (showPalette)
                {
                    RebuildZonePaletteButtons(zoneId, state);
                }
            }

            if (_zoneSpeedSliders.TryGetValue(zoneId, out Slider? speedSlider))
            {
                bool restoringZoneSliderState = _loadingLightingState;
                _loadingLightingState = true;
                try
                {
                    speedSlider.Value = Math.Clamp(state.Speed, 0, 100);
                }
                finally
                {
                    _loadingLightingState = restoringZoneSliderState;
                }

                speedSlider.IsEnabled = LightingEffectCatalog.IsAnimated(state.Effect);
            }

            if (_zoneSpeedTexts.TryGetValue(zoneId, out TextBlock? speedText))
            {
                speedText.Text = $"{Math.Clamp(state.Speed, 0, 100)}%";
                speedText.Opacity = LightingEffectCatalog.IsAnimated(state.Effect) ? 1 : 0.45;
            }

            if (_zoneBrightnessSliders.TryGetValue(zoneId, out Slider? brightnessSlider))
            {
                bool restoringZoneBrightnessState = _loadingLightingState;
                _loadingLightingState = true;
                try
                {
                    brightnessSlider.Value = Math.Clamp(state.Brightness, 0, 100);
                }
                finally
                {
                    _loadingLightingState = restoringZoneBrightnessState;
                }
            }

            if (_zoneBrightnessTexts.TryGetValue(zoneId, out TextBlock? brightnessText))
            {
                brightnessText.Text = $"{Math.Clamp(state.Brightness, 0, 100)}%";
            }
        }

        bool wasLoadingLightingState = _loadingLightingState;
        _loadingLightingState = true;
        try
        {
            foreach ((int zoneId, WpfComboBox combo) in _zoneEffectCombos)
            {
                LightingEffect effect = _draftZoneStates.TryGetValue(zoneId, out ZoneLightingState? state)
                    ? LightingEffectCatalog.NormalizeEffect(_lightingProfile, state.Effect)
                    : LightingEffectCatalog.GetDefaultEffect(_lightingProfile);
                combo.ItemsSource = LightingEffectCatalog.GetSupportedEffects(_lightingProfile);
                combo.SelectedItem = effect;
            }
        }
        finally
        {
            _loadingLightingState = wasLoadingLightingState;
        }

        foreach ((int zoneId, Border card) in _zoneEditorCards)
        {
            bool active = _allZonesSelected || _activeZoneId == zoneId;
            card.BorderBrush = new SolidColorBrush(active ? AccentStrong : MediaColor.FromRgb(67, 94, 130));
            card.BorderThickness = active ? new Thickness(1.6) : new Thickness(1);
            card.Background = new SolidColorBrush(active
                ? MediaColor.FromArgb(_allZonesSelected ? (byte)148 : (byte)132, 28, 88, 112)
                : MediaColor.FromArgb(81, 34, 50, 69));
        }
    }

    private void UpdateZoneActionButtons()
    {
        bool hasZones = _lightingProfile is not null && _lightingProfile.Zones.Count > 0;
        SyncAllButton.Content = hasZones
            ? _allZonesSelected ? "All Sections Selected" : "Select All Sections"
            : "Select All";
        SyncAllButton.IsEnabled = hasZones;
        UndoLightingButton.IsEnabled = (_undoLightingSnapshot ?? _committedLightingSnapshot) is not null;
        UndoLightingButton.Content = _lightingDirty ? "Undo Unsaved" : "Undo";
    }

    private void RebuildZonePaletteButtons(int zoneId, ZoneLightingState state)
    {
        if (!_zonePaletteWraps.TryGetValue(zoneId, out WrapPanel? paletteWrap))
        {
            return;
        }

        paletteWrap.Children.Clear();
        IReadOnlyList<RgbColor> palette = (state.Palette is { Count: > 0 } ? state.Palette : LightingEffectCatalog.DefaultSpectrumPalette)
            .Take(7)
            .ToArray();

        for (int index = 0; index < palette.Count; index++)
        {
            WpfButton colorButton = new()
            {
                Tag = Tuple.Create(zoneId, index),
                Width = 34,
                Height = 28,
                Margin = new Thickness(0, 0, 6, 6),
                Style = (Style)FindResource("GlassButtonStyle"),
                FontSize = 10,
            };
            colorButton.Click += ZoneSpectrumColorButton_Click;
            UpdateColorButton(colorButton, FromRgb(palette[index]));
            paletteWrap.Children.Add(colorButton);
        }

        if (palette.Count < 7)
        {
            WpfButton addButton = new()
            {
                Tag = zoneId,
                Width = 34,
                Height = 28,
                Margin = new Thickness(0, 0, 6, 6),
                Style = (Style)FindResource("GlassButtonStyle"),
                Content = "+",
                FontSize = 13,
            };
            addButton.Click += ZoneSpectrumAddColorButton_Click;
            paletteWrap.Children.Add(addButton);
        }

        if (palette.Count > 2)
        {
            WpfButton removeButton = new()
            {
                Tag = zoneId,
                Width = 34,
                Height = 28,
                Margin = new Thickness(0, 0, 6, 6),
                Style = (Style)FindResource("GlassButtonStyle"),
                Content = "-",
                FontSize = 13,
            };
            removeButton.Click += ZoneSpectrumRemoveColorButton_Click;
            paletteWrap.Children.Add(removeButton);
        }
    }

    private static IReadOnlyList<int?> BuildDeckCells(LightingDeviceProfile? profile, out int columns, out int rows)
    {
        if (profile?.PreviewGrid is { } previewGrid &&
            previewGrid.Columns > 0 &&
            previewGrid.Rows > 0 &&
            previewGrid.Cells.Count == previewGrid.Columns * previewGrid.Rows)
        {
            columns = previewGrid.Columns;
            rows = previewGrid.Rows;
            return previewGrid.Cells;
        }

        IReadOnlyList<LightingZoneDefinition> zones = profile?.Zones ?? [];
        if (zones.Count == 0)
        {
            columns = 1;
            rows = 1;
            return [null];
        }

        columns = Math.Min(Math.Max(zones.Count, 1), 4);
        rows = (int)Math.Ceiling(zones.Count / (double)columns);
        int?[] cells = new int?[rows * columns];
        for (int index = 0; index < zones.Count && index < cells.Length; index++)
        {
            cells[index] = zones[index].ZoneId;
        }

        return cells;
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
        CheckUpdatesButton.IsEnabled = !_checkingForUpdates && !_installingUpdate;
        OpenReleaseButton.IsEnabled = !_installingUpdate;
        DownloadInstallerButton.IsEnabled = !_installingUpdate;

        if (_checkingForUpdates)
        {
            UpdateStatusText.Text = "Checking GitHub Releases for a newer build...";
            UpdateWarningText.Visibility = Visibility.Collapsed;
            OpenReleaseButton.Visibility = Visibility.Collapsed;
            DownloadInstallerButton.Visibility = Visibility.Collapsed;
            ReleaseNotesText.Visibility = Visibility.Collapsed;
            return;
        }

        if (_installingUpdate)
        {
            UpdateStatusText.Text = "Downloading the installer and starting setup...";
            UpdateWarningText.Visibility = Visibility.Collapsed;
            OpenReleaseButton.Visibility = Visibility.Collapsed;
            DownloadInstallerButton.Visibility = Visibility.Visible;
            DownloadInstallerButton.Content = "Installing...";
            ReleaseNotesText.Visibility = Visibility.Collapsed;
            return;
        }

        DownloadInstallerButton.Content = "Install update";

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
            Icon = LoadNotifyIcon(),
            ContextMenuStrip = menu,
            Visible = false,
        };

        notifyIcon.DoubleClick += TrayOpen_Click;
        return notifyIcon;
    }

    private static System.Drawing.Icon LoadNotifyIcon()
    {
        try
        {
            string? executablePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
            {
                System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch
        {
        }

        return System.Drawing.SystemIcons.Application;
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
        if (!_allowExit && _desktopSettings.MinimizeToTray)
        {
            e.Cancel = true;
            HideToTray(showNotification: false);
            return;
        }

        if (_allowExit)
        {
            return;
        }

        if (_lightingDirty)
        {
            MessageBoxResult result = WpfMessageBox.Show(
                this,
                "Lighting changes are already previewed on the keyboard but are not saved yet.\n\nYes: save and close\nNo: close without saving\nCancel: keep editing",
                "AlienFx Lite",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (result == MessageBoxResult.Yes)
            {
                e.Cancel = true;
                Dispatcher.InvokeAsync(async () =>
                {
                    if (await SaveLightingChangesAsync(showErrors: true, refreshBrokerStatus: true).ConfigureAwait(true))
                    {
                        _allowExit = true;
                        Close();
                    }
                });
                return;
            }

            return;
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _lightingController.Dispose();
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

    private static System.Windows.Media.Brush BuildKeyBrush(bool selected, bool enabled, ZoneLightingState state)
    {
        MediaColor primary = ToMediaColor(ScaleColor(state.PrimaryColor, state.Brightness));
        MediaColor secondary = state.SecondaryColor is { } secondaryColor
            ? ToMediaColor(ScaleColor(secondaryColor, state.Brightness))
            : Colors.Black;

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

        if (state.Effect == LightingEffect.Morph)
        {
            return new LinearGradientBrush(
                WithAlpha(Lighten(primary, 0.18), 224),
                WithAlpha(Lighten(secondary, 0.12), 206),
                new WpfPoint(0, 0),
                new WpfPoint(1, 1));
        }

        if (state.Effect == LightingEffect.Spectrum)
        {
            IReadOnlyList<RgbColor> palette = (state.Palette is { Count: > 0 } ? state.Palette : LightingEffectCatalog.DefaultSpectrumPalette)
                .Take(7)
                .ToArray();

            GradientStopCollection stops = [];
            for (int index = 0; index < palette.Count; index++)
            {
                MediaColor paletteColor = ToMediaColor(ScaleColor(palette[index], state.Brightness));
                double offset = palette.Count == 1 ? 1 : index / (double)(palette.Count - 1);
                stops.Add(new GradientStop(WithAlpha(paletteColor, 220), offset));
            }

            return new LinearGradientBrush(stops, new WpfPoint(0, 0), new WpfPoint(1, 1));
        }

        if (state.Effect == LightingEffect.Rainbow)
        {
            GradientStopCollection stops =
            [
                new GradientStop(WithAlpha(MediaColor.FromRgb(255, 87, 87), 226), 0.00),
                new GradientStop(WithAlpha(MediaColor.FromRgb(255, 197, 91), 214), 0.22),
                new GradientStop(WithAlpha(MediaColor.FromRgb(130, 244, 104), 214), 0.48),
                new GradientStop(WithAlpha(MediaColor.FromRgb(94, 220, 255), 220), 0.74),
                new GradientStop(WithAlpha(MediaColor.FromRgb(209, 132, 255), 212), 1.00),
            ];

            return new LinearGradientBrush(stops, new WpfPoint(0, 0), new WpfPoint(1, 1));
        }

        return new LinearGradientBrush(
            WithAlpha(Lighten(primary, 0.18), state.Effect is LightingEffect.Pulse or LightingEffect.Breathing ? (byte)212 : (byte)232),
            WithAlpha(Darken(primary, 0.12), state.Effect is LightingEffect.Pulse or LightingEffect.Breathing ? (byte)182 : (byte)214),
            new WpfPoint(0, 0),
            new WpfPoint(1, 1));
    }

    private static MediaColor ToMediaColor(RgbColor color) => MediaColor.FromRgb(color.R, color.G, color.B);

    private static RgbColor ScaleColor(RgbColor color, int brightness)
    {
        double factor = Math.Clamp(brightness, 0, 100) / 100d;
        return new RgbColor(
            (byte)Math.Clamp(Math.Round(color.R * factor), 0, 255),
            (byte)Math.Clamp(Math.Round(color.G * factor), 0, 255),
            (byte)Math.Clamp(Math.Round(color.B * factor), 0, 255));
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

    private sealed record KeyCapCell(int ZoneId, Border Element);
}
