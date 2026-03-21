using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using AlienFxLite.Contracts;
using WinForms = System.Windows.Forms;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;
using WpfButton = System.Windows.Controls.Button;

namespace AlienFxLite.UI;

public partial class MainWindow : Window
{
    private static readonly MediaColor Accent = MediaColor.FromRgb(0, 214, 255);
    private static readonly MediaColor AccentStrong = MediaColor.FromRgb(0, 255, 200);
    private static readonly MediaColor Danger = MediaColor.FromRgb(255, 120, 150);

    private readonly AlienFxLiteServiceClient _client = new();
    private readonly WinForms.ColorDialog _colorDialog = new() { FullOpen = true };
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Dictionary<LightingZone, ToggleButton> _zoneButtons;

    private DrawingColor _primaryColor = DrawingColor.White;
    private DrawingColor _secondaryColor = DrawingColor.Black;
    private bool _refreshing;
    private bool _loadingLightingState;
    private bool _lightingDirty;

    public MainWindow()
    {
        InitializeComponent();

        _zoneButtons = new Dictionary<LightingZone, ToggleButton>
        {
            [LightingZone.KbLeft] = LeftZoneButton,
            [LightingZone.KbCenter] = CenterZoneButton,
            [LightingZone.KbRight] = RightZoneButton,
            [LightingZone.KbNumPad] = NumPadZoneButton,
        };

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

        UpdateColorButton(PrimaryColorButton, _primaryColor);
        UpdateColorButton(SecondaryColorButton, _secondaryColor);
        UpdateTrackLabels();
        UpdateEffectUi();
        UpdateApplyButtonState();

        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync(silent: true, preservePendingLighting: true).ConfigureAwait(true);
        Closed += (_, _) => _refreshTimer.Stop();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync(silent: true, preservePendingLighting: false).ConfigureAwait(true);
        _refreshTimer.Start();
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
                System.Windows.MessageBox.Show(this, "Select at least one keyboard zone.", "AlienFx Lite", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            System.Windows.MessageBox.Show(this, ex.Message, "AlienFx Lite", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void FanAutoButton_Click(object sender, RoutedEventArgs e)
    {
        await ApplyFanModeAsync(FanControlMode.Auto).ConfigureAwait(true);
    }

    private async void FanMaxButton_Click(object sender, RoutedEventArgs e)
    {
        await ApplyFanModeAsync(FanControlMode.Max).ConfigureAwait(true);
    }

    private void PrimaryColorButton_Click(object sender, RoutedEventArgs e)
    {
        ChooseColor(true);
    }

    private void SecondaryColorButton_Click(object sender, RoutedEventArgs e)
    {
        ChooseColor(false);
    }

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

    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        MarkLightingDirty();
    }

    private void ZoneButton_Changed(object sender, RoutedEventArgs e)
    {
        MarkLightingDirty();
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

            if (!silent)
            {
                System.Windows.MessageBox.Show(this, ex.Message, "AlienFx Lite", MessageBoxButton.OK, MessageBoxImage.Error);
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
            System.Windows.MessageBox.Show(this, ex.Message, "AlienFx Lite", MessageBoxButton.OK, MessageBoxImage.Error);
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
            UpdateTrackLabels();
            UpdateEffectUi();
            UpdateApplyButtonState();
        }

        PendingText.Text = _lightingDirty
            ? "Pending local edits. Press Apply Lighting to commit them."
            : "No pending local edits.";
        PendingText.Foreground = new SolidColorBrush(_lightingDirty ? Accent : Colors.White);
        LightingHintText.Text = $"Effect: {EffectCombo.SelectedItem}  |  Brightness: {(int)BrightnessSlider.Value}%  |  KeepAlive: {(KeepAliveCheck.IsChecked == true ? "on" : "off")}";
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
    }

    private void UpdateApplyButtonState()
    {
        ApplyLightingButton.Content = _lightingDirty ? "APPLY LIGHTING *" : "APPLY LIGHTING";
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

    private static void UpdateColorButton(WpfButton button, DrawingColor color)
    {
        button.Content = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        button.Background = new SolidColorBrush(MediaColor.FromRgb(color.R, color.G, color.B));
        button.Foreground = color.GetBrightness() < 0.45f ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Black;
    }

    private static RgbColor ToRgb(DrawingColor color) => new((byte)color.R, (byte)color.G, (byte)color.B);

    private static DrawingColor FromRgb(RgbColor color) => DrawingColor.FromArgb(color.R, color.G, color.B);
}
