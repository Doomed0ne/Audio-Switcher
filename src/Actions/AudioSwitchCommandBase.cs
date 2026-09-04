namespace Loupedeck.AudioSwitcherPlugin;

using System;
using System.Threading;

public abstract class AudioSwitchCommandBase : MultistateActionEditorCommand
{
    // ── Action editor control names ──────────────────────────────────────────
    private const String DeviceControlName = "Device";
    private const String DeviceDisplayName = "Select Device:";

    private const String LayoutControlName = "BadgePosition";
    private const String LayoutDisplayName = "Badge Position:";

    // ── State ────────────────────────────────────────────────────────────────
    private readonly Timer _refreshTimer;
    private volatile String _lastDefaultKey;

    // The profile arrives after the plugin during Windows startup. Retry briefly
    // until a saved action image has actually been refreshed.
    private Int32 _startupRefreshAttemptsRemaining = 20;

    protected AudioSwitchCommandBase(String displayName, String description)
        : base(DeviceType.All)
    {
        this.DisplayName = displayName;
        this.Description = description;
        this.GroupName   = "Audio";

        // Register two states: 0 = Inactive (Unselected), 1 = Active (Selected)
        this.AddState("Inactive", "Unselected", "Device is not currently active");
        this.AddState("Active", "Selected", "Device is currently the active default");

        // Device selector
        this.ActionEditor.AddControlEx(
            new ActionEditorListbox(DeviceControlName, DeviceDisplayName));

        // 3x3 matrix badge position selector
        this.ActionEditor.AddControlEx(
            new ActionEditorListbox(LayoutControlName, LayoutDisplayName));

        this.ActionEditor.ListboxItemsRequested += this.OnListboxItemsRequested;

        this._refreshTimer   = new Timer(this.OnTimerTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    protected abstract EDataFlow DataFlow { get; }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    protected override Boolean OnLoad()
    {
        PluginLog.Info($"{this.DisplayName} OnLoad");
        this._lastDefaultKey = this.ReadDefaultDeviceKey();
        if (!String.IsNullOrEmpty(this._lastDefaultKey))
        {
            ProfileImageBadgeSynchronizer.Synchronize(this.DataFlow, this._lastDefaultKey);
        }

        this._startupRefreshAttemptsRemaining = 40;
        this._refreshTimer.Change(100, 250);
        return true;
    }

    protected override Boolean OnUnload()
    {
        PluginLog.Info($"{this.DisplayName} OnUnload");
        this._refreshTimer.Change(Timeout.Infinite, Timeout.Infinite);
        return true;
    }

    // ── Timer ────────────────────────────────────────────────────────────────

    private void OnTimerTick(Object state)
    {
        try
        {
            if (this.Plugin == null)
            {
                return;
            }

            var currentKey = this.ReadDefaultDeviceKey();

            if (this._startupRefreshAttemptsRemaining > 0)
            {
                this._startupRefreshAttemptsRemaining--;
                var synchronized = !String.IsNullOrEmpty(currentKey) &&
                                   ProfileImageBadgeSynchronizer.Synchronize(this.DataFlow, currentKey);
                var notified = this.TryNotifyImageChanged();

                if (!String.IsNullOrEmpty(currentKey))
                {
                    this._lastDefaultKey = currentKey;
                }

                if ((synchronized && notified) || this._startupRefreshAttemptsRemaining == 0)
                {
                    this.CompleteStartupWarmup();
                }

                return;
            }

            // Steady-state: synchronize on every 1s tick so newly created actions,
            // edited icons, and device changes are immediately reflected.
            if (!String.IsNullOrEmpty(currentKey))
            {
                var defaultChanged = !String.Equals(this._lastDefaultKey, currentKey, StringComparison.OrdinalIgnoreCase);
                var synchronized = ProfileImageBadgeSynchronizer.Synchronize(this.DataFlow, currentKey);

                if (defaultChanged || synchronized)
                {
                    this.TryNotifyImageChanged();
                    this._lastDefaultKey = currentKey;
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, $"Default device polling failed: {ex.Message}");
        }
    }

    // ── Listbox population ───────────────────────────────────────────────────

    private void OnListboxItemsRequested(Object sender, ActionEditorListboxItemsRequestedEventArgs e)
    {
        if (e.ControlName.EqualsNoCase(DeviceControlName))
        {
            try
            {
                foreach (var device in AudioDeviceManager.GetDevices(this.DataFlow))
                {
                    e.AddItem(device.Key, device.Name, device.Name);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"Failed to list devices: {ex.Message}");
            }
            return;
        }

        if (e.ControlName.EqualsNoCase(LayoutControlName))
        {
            e.AddItem("top-right",     "Top Right (Default)",                        "Top Right");
            e.AddItem("top-center",    "Top Center",                                 "Top Center");
            e.AddItem("top-left",      "Top Left",                                   "Top Left");
            e.AddItem("middle-right",  "Middle Right",                               "Middle Right");
            e.AddItem("center",        "Center",                                     "Center");
            e.AddItem("middle-left",   "Middle Left",                                "Middle Left");
            e.AddItem("bottom-right",  "Bottom Right (Recommended for text on top)", "Bottom Right");
            e.AddItem("bottom-center", "Bottom Center",                              "Bottom Center");
            e.AddItem("bottom-left",   "Bottom Left",                                "Bottom Left");
            e.AddItem("none",          "None (Manual 2-State Icons)",                "None");
        }
    }

    // ── Command execution ────────────────────────────────────────────────────

    protected override Boolean RunCommand(ActionEditorActionParameters actionParameters)
    {
        if (!actionParameters.TryGetString(DeviceControlName, out var deviceId))
        {
            return false;
        }

        try
        {
            PluginLog.Info($"RunCommand executing for device: {deviceId}");
            if (AudioDeviceManager.SetDefault(deviceId))
            {
                this._lastDefaultKey = deviceId;
                this.SetCurrentState(actionParameters, 1);
                ProfileImageBadgeSynchronizer.Synchronize(this.DataFlow, deviceId);
                this.TryNotifyImageChanged();
                return true;
            }

            PluginLog.Warning($"Device not found or switch failed: {deviceId}");
            return false;
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, $"Switch failed: {ex.Message}");
            return false;
        }
    }

    // ── Image rendering (Multistate) ─────────────────────────────────────────

    protected override BitmapImage GetCommandImage(
        ActionEditorActionParameters actionParameters,
        Int32 stateIndex,
        Int32 imageWidth,
        Int32 imageHeight)
    {
        try
        {
            String deviceId = null;
            var hasDevice = actionParameters != null &&
                            actionParameters.TryGetString(DeviceControlName, out deviceId) &&
                            !String.IsNullOrEmpty(deviceId);

            // The timer owns Windows device detection. Rendering uses its last known
            // value so Loupedeck image callbacks do not fan out concurrent COM calls.
            var isCurrentDefault = hasDevice && String.Equals(
                deviceId,
                this._lastDefaultKey,
                StringComparison.OrdinalIgnoreCase);
            var renderStateIndex = isCurrentDefault ? 1 : 0;

            // Correct Loupedeck's stored state when it diverges from Windows reality.
            // This is safe to call inside GetCommandImage and ensures the profile's
            // stored state converges to the correct value across all warmup cycles.
            if (hasDevice && renderStateIndex != stateIndex)
            {
                this.SetCurrentState(actionParameters, renderStateIndex);
            }

            var baseImage = base.GetCommandImage(actionParameters, renderStateIndex, imageWidth, imageHeight)
                         ?? base.GetCommandImage(actionParameters, 0, imageWidth, imageHeight);
            var displayName = this.GetCommandDisplayName(actionParameters, renderStateIndex);
            var badgePosition = renderStateIndex == 1
                ? this.ResolveBadgePosition(actionParameters)
                : BadgePosition.None;

            return AudioDeviceSwitchImage.BuildCommandButtonImage(
                displayName,
                renderStateIndex,
                baseImage,
                null,
                imageWidth,
                imageHeight,
                badgePosition);
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, $"GetCommandImage error: {ex.Message}");
            return null;
        }
    }

    protected override String GetCommandDisplayName(
        ActionEditorActionParameters actionParameters,
        Int32 stateIndex)
    {
        return String.Empty;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private String ReadDefaultDeviceKey() => AudioDeviceManager.GetDefaultDevice(this.DataFlow)?.Key;

    private void CompleteStartupWarmup()
    {
        if (this._startupRefreshAttemptsRemaining < 0)
        {
            return;
        }

        this._startupRefreshAttemptsRemaining = -1;
        PluginLog.Info($"{this.DisplayName} startup badge refresh complete, switching to 1s poll");
        this._refreshTimer.Change(1000, 1000);
    }

    private Boolean TryNotifyImageChanged()
    {
        try
        {
            this.ActionImageChanged();
            return true;
        }
        catch (NullReferenceException)
        {
            // SDK has no image-change subscriber yet while the profile is still loading.
            return false;
        }
    }

    private BadgePosition ResolveBadgePosition(ActionEditorActionParameters actionParameters)
    {
        if (actionParameters != null && actionParameters.TryGetString(LayoutControlName, out var lv))
        {
            return lv.ToLowerInvariant() switch
            {
                "top-center"    => BadgePosition.TopCenter,
                "top-left"      => BadgePosition.TopLeft,
                "middle-right"  => BadgePosition.MiddleRight,
                "center"        => BadgePosition.Center,
                "middle-left"   => BadgePosition.MiddleLeft,
                "bottom-right"  => BadgePosition.BottomRight,
                "bottom-center" => BadgePosition.BottomCenter,
                "bottom-left"   => BadgePosition.BottomLeft,
                "none"          => BadgePosition.None,
                _               => BadgePosition.TopRight,
            };
        }

        return BadgePosition.TopRight;
    }
}
