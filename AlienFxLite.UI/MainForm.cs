using System.Drawing;
using AlienFxLite.Contracts;

namespace AlienFxLite.UI;

internal sealed class MainForm : Form
{
    private readonly AlienFxLiteServiceClient _client = new();
    private readonly ColorDialog _colorDialog = new();
    private readonly Dictionary<LightingZone, CheckBox> _zoneChecks = new();
    private readonly Label _serviceStatusLabel = new() { AutoSize = true };
    private readonly Label _deviceStatusLabel = new() { AutoSize = true };
    private readonly Label _fanModeLabel = new() { AutoSize = true };
    private readonly Label _fanRpmLabel = new() { AutoSize = true };
    private readonly ComboBox _effectCombo = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _primaryButton = new() { Dock = DockStyle.Fill, Text = "Primary" };
    private readonly Button _secondaryButton = new() { Dock = DockStyle.Fill, Text = "Secondary" };
    private readonly TrackBar _speedTrack = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, TickFrequency = 10, Value = 50 };
    private readonly TrackBar _brightnessTrack = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, TickFrequency = 10, Value = 100 };
    private readonly Label _speedValueLabel = new() { AutoSize = true };
    private readonly Label _brightnessValueLabel = new() { AutoSize = true };
    private readonly CheckBox _keepAliveCheck = new() { AutoSize = true, Text = "Maintain lights", Checked = true };
    private readonly CheckBox _enabledCheck = new() { AutoSize = true, Text = "Lights enabled", Checked = true };
    private readonly Button _applyLightingButton = new() { Text = "Apply Lights", Dock = DockStyle.Fill };
    private readonly Button _refreshButton = new() { Text = "Refresh", Dock = DockStyle.Fill };
    private readonly Button _fanAutoButton = new() { Text = "Auto", Dock = DockStyle.Fill };
    private readonly Button _fanMaxButton = new() { Text = "Max", Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 5000 };

    private readonly Label _secondaryLabel = new() { Text = "Secondary Color", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _speedLabel = new() { Text = "Effect Speed", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };

    private Color _primaryColor = Color.White;
    private Color _secondaryColor = Color.Black;
    private bool _refreshing;
    private bool _loadingLightingState;
    private bool _lightingDirty;

    public MainForm()
    {
        Text = "AlienFx Lite";
        MinimumSize = new Size(720, 480);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        BindEvents();

        _effectCombo.Items.AddRange(Enum.GetValues<LightingEffect>().Cast<object>().ToArray());
        _effectCombo.SelectedItem = LightingEffect.Static;

        UpdateColorButton(_primaryButton, _primaryColor);
        UpdateColorButton(_secondaryButton, _secondaryColor);
        UpdateTrackLabels();
        UpdateEffectUi();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        await RefreshStatusAsync(silent: true, preservePendingLighting: false).ConfigureAwait(true);
        _refreshTimer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        base.OnFormClosed(e);
    }

    private void BuildLayout()
    {
        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        TableLayoutPanel header = new()
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        FlowLayoutPanel statusPanel = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };
        statusPanel.Controls.Add(_serviceStatusLabel);
        statusPanel.Controls.Add(_deviceStatusLabel);
        header.Controls.Add(statusPanel, 0, 0);
        header.Controls.Add(_refreshButton, 1, 0);

        TableLayoutPanel content = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));

        GroupBox lightsGroup = BuildLightsGroup();
        GroupBox fanGroup = BuildFansGroup();
        content.Controls.Add(lightsGroup, 0, 0);
        content.Controls.Add(fanGroup, 1, 0);

        Label footer = new()
        {
            Dock = DockStyle.Fill,
            Text = "The UI stays unelevated. Fan and lighting commands go through the local broker service.",
            AutoSize = true,
        };

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(content, 0, 1);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
    }

    private GroupBox BuildLightsGroup()
    {
        GroupBox group = new()
        {
            Dock = DockStyle.Fill,
            Text = "Lights",
            Padding = new Padding(12),
        };

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 7,
            AutoSize = true,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        FlowLayoutPanel zonesPanel = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
        };
        AddZoneCheckbox(zonesPanel, LightingZone.KbLeft, "KB Left");
        AddZoneCheckbox(zonesPanel, LightingZone.KbCenter, "KB Center");
        AddZoneCheckbox(zonesPanel, LightingZone.KbRight, "KB Right");
        AddZoneCheckbox(zonesPanel, LightingZone.KbNumPad, "KB NumPad");

        layout.Controls.Add(new Label { Text = "Zones", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        layout.Controls.Add(zonesPanel, 1, 0);
        layout.Controls.Add(new Label { Text = "Effect", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        layout.Controls.Add(_effectCombo, 1, 1);
        layout.Controls.Add(new Label { Text = "Primary Color", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
        layout.Controls.Add(_primaryButton, 1, 2);
        layout.Controls.Add(_secondaryLabel, 0, 3);
        layout.Controls.Add(_secondaryButton, 1, 3);

        FlowLayoutPanel speedPanel = new() { Dock = DockStyle.Fill, AutoSize = true };
        speedPanel.Controls.Add(_speedTrack);
        speedPanel.Controls.Add(_speedValueLabel);
        layout.Controls.Add(_speedLabel, 0, 4);
        layout.Controls.Add(speedPanel, 1, 4);

        FlowLayoutPanel brightnessPanel = new() { Dock = DockStyle.Fill, AutoSize = true };
        brightnessPanel.Controls.Add(_brightnessTrack);
        brightnessPanel.Controls.Add(_brightnessValueLabel);
        layout.Controls.Add(new Label { Text = "Brightness", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 5);
        layout.Controls.Add(brightnessPanel, 1, 5);

        FlowLayoutPanel optionsPanel = new() { Dock = DockStyle.Fill, AutoSize = true };
        optionsPanel.Controls.Add(_keepAliveCheck);
        optionsPanel.Controls.Add(_enabledCheck);
        optionsPanel.Controls.Add(_applyLightingButton);
        layout.Controls.Add(new Label { Text = "Options", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 6);
        layout.Controls.Add(optionsPanel, 1, 6);

        group.Controls.Add(layout);
        return group;
    }

    private GroupBox BuildFansGroup()
    {
        GroupBox group = new()
        {
            Dock = DockStyle.Fill,
            Text = "Fans",
            Padding = new Padding(12),
        };

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
        };

        layout.Controls.Add(_fanModeLabel, 0, 0);
        layout.Controls.Add(_fanRpmLabel, 0, 1);

        FlowLayoutPanel buttons = new() { Dock = DockStyle.Top, AutoSize = true };
        buttons.Controls.Add(_fanAutoButton);
        buttons.Controls.Add(_fanMaxButton);
        layout.Controls.Add(buttons, 0, 2);

        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Use Auto to return control to BIOS. Use Max to force 100 raw boost on every detected fan.",
        }, 0, 3);

        group.Controls.Add(layout);
        return group;
    }

    private void BindEvents()
    {
        _primaryButton.Click += (_, _) => ChooseColor(_primaryButton, isPrimary: true);
        _secondaryButton.Click += (_, _) => ChooseColor(_secondaryButton, isPrimary: false);
        _effectCombo.SelectedValueChanged += (_, _) =>
        {
            UpdateEffectUi();
            MarkLightingDirty();
        };
        _speedTrack.Scroll += (_, _) =>
        {
            UpdateTrackLabels();
            MarkLightingDirty();
        };
        _brightnessTrack.Scroll += (_, _) =>
        {
            UpdateTrackLabels();
            MarkLightingDirty();
        };
        _keepAliveCheck.CheckedChanged += (_, _) => MarkLightingDirty();
        _enabledCheck.CheckedChanged += (_, _) => MarkLightingDirty();
        _applyLightingButton.Click += async (_, _) => await ApplyLightingAsync().ConfigureAwait(true);
        _fanAutoButton.Click += async (_, _) => await ApplyFanModeAsync(FanControlMode.Auto).ConfigureAwait(true);
        _fanMaxButton.Click += async (_, _) => await ApplyFanModeAsync(FanControlMode.Max).ConfigureAwait(true);
        _refreshButton.Click += async (_, _) => await RefreshStatusAsync(silent: false, preservePendingLighting: false).ConfigureAwait(true);
        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync(silent: true, preservePendingLighting: true).ConfigureAwait(true);
    }

    private void AddZoneCheckbox(Control parent, LightingZone zone, string text)
    {
        CheckBox check = new()
        {
            Text = text,
            AutoSize = true,
            Checked = true,
        };
        _zoneChecks[zone] = check;
        check.CheckedChanged += (_, _) => MarkLightingDirty();
        parent.Controls.Add(check);
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
            _serviceStatusLabel.Text = "Service: unavailable";
            _deviceStatusLabel.Text = ex.Message;
            _fanModeLabel.Text = "Fan mode: unavailable";
            _fanRpmLabel.Text = "RPM: n/a";

            if (!silent)
            {
                MessageBox.Show(this, ex.Message, "AlienFx Lite", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task ApplyLightingAsync()
    {
        try
        {
            List<LightingZone> zones = _zoneChecks.Where(pair => pair.Value.Checked).Select(pair => pair.Key).ToList();
            if (zones.Count == 0)
            {
                MessageBox.Show(this, "Select at least one zone.", "AlienFx Lite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            LightingEffect effect = (LightingEffect)(_effectCombo.SelectedItem ?? LightingEffect.Static);
            SetLightingStateRequest request = new(
                zones,
                effect,
                ToRgb(_primaryColor),
                effect == LightingEffect.Morph ? ToRgb(_secondaryColor) : null,
                _speedTrack.Value,
                _brightnessTrack.Value,
                _keepAliveCheck.Checked,
                _enabledCheck.Checked);

            await _client.SetLightingStateAsync(request).ConfigureAwait(true);
            _lightingDirty = false;
            UpdateApplyButtonState();
            await RefreshStatusAsync(silent: true, preservePendingLighting: false).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "AlienFx Lite", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            MessageBox.Show(this, ex.Message, "AlienFx Lite", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ApplyStatus(StatusSnapshot status, bool preservePendingLighting)
    {
        _serviceStatusLabel.Text = "Service: connected";
        _deviceStatusLabel.Text = $"Lighting: {(status.Devices.LightingAvailable ? status.Devices.LightingDevice : "unavailable")} | Fans: {(status.Devices.FanAvailable ? status.Devices.FanProvider : "unavailable")}";
        _fanModeLabel.Text = $"Fan mode: {status.Fan.Mode}";
        _fanRpmLabel.Text = status.Fan.Rpm.Count > 0
            ? $"RPM: {string.Join(" / ", status.Fan.Rpm)}"
            : $"RPM: n/a ({status.Fan.Message})";

        if (preservePendingLighting)
        {
            return;
        }

        _loadingLightingState = true;
        try
        {
        _brightnessTrack.Value = Math.Clamp(status.Lighting.Brightness, _brightnessTrack.Minimum, _brightnessTrack.Maximum);
        _keepAliveCheck.Checked = status.Lighting.KeepAlive;
        _enabledCheck.Checked = status.Lighting.Enabled;

        ZoneLightingState? reference = status.Lighting.ZoneStates.OrderBy(static zone => zone.Zone).FirstOrDefault();
        if (reference is not null)
        {
            _effectCombo.SelectedItem = reference.Effect;
            _speedTrack.Value = Math.Clamp(reference.Speed, _speedTrack.Minimum, _speedTrack.Maximum);
            _primaryColor = Color.FromArgb(reference.PrimaryColor.R, reference.PrimaryColor.G, reference.PrimaryColor.B);
            _secondaryColor = reference.SecondaryColor is null
                ? Color.Black
                : Color.FromArgb(reference.SecondaryColor.Value.R, reference.SecondaryColor.Value.G, reference.SecondaryColor.Value.B);
            UpdateColorButton(_primaryButton, _primaryColor);
            UpdateColorButton(_secondaryButton, _secondaryColor);
        }

            HashSet<LightingZone> activeZones = status.Lighting.ZoneStates
                .Where(static zone => zone.Enabled)
                .Select(static zone => zone.Zone)
                .ToHashSet();
            foreach ((LightingZone zone, CheckBox check) in _zoneChecks)
            {
                check.Checked = activeZones.Contains(zone);
            }

        UpdateTrackLabels();
        UpdateEffectUi();
        }
        finally
        {
            _loadingLightingState = false;
        }

        _lightingDirty = false;
        UpdateApplyButtonState();
    }

    private void ChooseColor(Button targetButton, bool isPrimary)
    {
        _colorDialog.Color = isPrimary ? _primaryColor : _secondaryColor;
        if (_colorDialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (isPrimary)
        {
            _primaryColor = _colorDialog.Color;
            UpdateColorButton(targetButton, _primaryColor);
        }
        else
        {
            _secondaryColor = _colorDialog.Color;
            UpdateColorButton(targetButton, _secondaryColor);
        }

        MarkLightingDirty();
    }

    private void UpdateTrackLabels()
    {
        _speedValueLabel.Text = _speedTrack.Value.ToString();
        _brightnessValueLabel.Text = _brightnessTrack.Value.ToString();
    }

    private void UpdateEffectUi()
    {
        LightingEffect effect = (LightingEffect)(_effectCombo.SelectedItem ?? LightingEffect.Static);
        bool usesSpeed = effect != LightingEffect.Static;
        bool usesSecondary = effect == LightingEffect.Morph;

        _speedTrack.Enabled = usesSpeed;
        _speedLabel.Enabled = usesSpeed;
        _speedValueLabel.Enabled = usesSpeed;
        _secondaryButton.Enabled = usesSecondary;
        _secondaryButton.Visible = usesSecondary;
        _secondaryLabel.Visible = usesSecondary;
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
        _applyLightingButton.Text = _lightingDirty ? "Apply Lights*" : "Apply Lights";
    }

    private static void UpdateColorButton(Button button, Color color)
    {
        button.BackColor = color;
        button.ForeColor = color.GetBrightness() < 0.45f ? Color.White : Color.Black;
        button.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static RgbColor ToRgb(Color color) => new((byte)color.R, (byte)color.G, (byte)color.B);
}
