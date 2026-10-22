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
    private Int32 _refreshInProgress;
    private Int32 _imageRefreshPending;

    // The profile can arrive after the plugin during Windows startup. Keep the short
    // warm-up window even if another saved profile was found first.
    private Int32 _startupRefreshAttemptsRemaining = 40;

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
        PluginLog.Info($"{this.DisplayName} OnLoad: Initializing audio device state");
        this.SynchronizeActiveDevice(this.ReadDefaultDeviceKey());

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
        if (Interlocked.Exchange(ref this._refreshInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            if (this.Plugin == null)
            {
                return;
            }

            if (Volatile.Read(ref this._imageRefreshPending) != 0)
            {
                this.TryNotifyImageChanged();
            }

            var currentKey = this.ReadDefaultDeviceKey();

            if (this._startupRefreshAttemptsRemaining > 0)
            {
                this._startupRefreshAttemptsRemaining--;
                this.SynchronizeActiveDevice(currentKey);

                if (this._startupRefreshAttemptsRemaining == 0)
                {
                    this.CompleteStartupWarmup();
                }

                return;
            }

            // Steady-state polling (1s): check if default device changed or profile actions were updated
            if (!String.IsNullOrEmpty(currentKey))
            {
                var defaultChanged = !String.Equals(this._lastDefaultKey, currentKey, StringComparison.OrdinalIgnoreCase);
                if (defaultChanged)
                {
                    this.SynchronizeActiveDevice(currentKey);
                }
                else
                {
                    // Periodically sync newly added/edited actions in profile
                    ProfileImageBadgeSynchronizer.Synchronize(
                        this.DataFlow,
                        currentKey,
                        onDeviceMatched: (actionParams, isCurrent) =>
                        {
                            this.SetDeviceState(actionParams, isCurrent ? 1 : 0);
                        },
                        onActionImagesChanged: () =>
                        {
                            this.TryNotifyImageChanged();
                        });
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, $"Default device polling failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref this._refreshInProgress, 0);
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

    private static readonly Object CommandLock = new();
    private static DateTime _lastCommandTime = DateTime.MinValue;
    private static String _lastCommandTarget = null;

    // Cache real ActionEditorActionParameters objects received from Loupedeck's
    // GetCommandImage callbacks. These are the EXACT internal objects Loupedeck
    // uses for GetHashCode64() lookups in its MultistateActionEditorCommand.f
    // dictionary. Calling SetCurrentState() with these objects guarantees the
    // correct hash slot is updated, whereas synthesized params may produce a
    // different hash if StringDictionaryNoCase internal ordering differs.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<String, ActionEditorActionParameters>
        _cachedRealParams = new(StringComparer.OrdinalIgnoreCase);

    protected override Boolean RunCommand(ActionEditorActionParameters actionParameters)
    {
        if (!actionParameters.TryGetString(DeviceControlName, out var deviceId))
        {
            return false;
        }

        // Cache the real Loupedeck-internal ActionEditorActionParameters object
        // for this device so we can call SetCurrentState on the exact object
        // Loupedeck uses for its hash-based state lookup.
        this._cachedRealParams[deviceId] = actionParameters;

        // Fast-click guard: if already the current active device, acknowledge immediately
        if (!String.IsNullOrEmpty(this._lastDefaultKey) &&
            String.Equals(this._lastDefaultKey, deviceId, StringComparison.OrdinalIgnoreCase))
        {
            PluginLog.Info($"Device '{deviceId}' is already active default; ignoring redundant switch.");
            this.ApplyCachedStateUpdates(deviceId);
            this.SynchronizeActiveDevice(deviceId);
            return true;
        }

        lock (CommandLock)
        {
            // Debounce: ignore rapid duplicate switches to the same target within 300ms
            var now = DateTime.UtcNow;
            if (String.Equals(_lastCommandTarget, deviceId, StringComparison.OrdinalIgnoreCase) &&
                (now - _lastCommandTime).TotalMilliseconds < 300)
            {
                PluginLog.Info($"Debouncing rapid switch for device: {deviceId}");
                return true;
            }

            _lastCommandTime = now;
            _lastCommandTarget = deviceId;

            try
            {
                PluginLog.Info($"RunCommand executing for device: {deviceId}");
                if (AudioDeviceManager.SetDefault(deviceId))
                {
                    // Immediately update Loupedeck's software UI using the real
                    // internal params objects (bypasses hash-guess via synthesized params).
                    this.ApplyCachedStateUpdates(deviceId);
                    this.SynchronizeActiveDevice(deviceId);
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
    }

    /// <summary>
    /// Applies state 1 to the newly active device and state 0 to all other cached
    /// devices using Loupedeck's real internal ActionEditorActionParameters objects.
    /// This ensures the software UI badge updates immediately after a button press.
    /// </summary>
    private void ApplyCachedStateUpdates(String activeDeviceId)
    {
        foreach (var pair in this._cachedRealParams)
        {
            var isActive = String.Equals(pair.Key, activeDeviceId, StringComparison.OrdinalIgnoreCase);
            this.SetDeviceState(pair.Value, isActive ? 1 : 0);
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

            // Cache the real Loupedeck-internal params object so RunCommand can
            // later call SetCurrentState on the exact object Loupedeck's hash
            // dictionary uses. This is the primary capture point — GetCommandImage
            // is called for every button render with the real internal object.
            if (hasDevice)
            {
                this._cachedRealParams[deviceId] = actionParameters;
            }

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

            var configuredBadgePosition = this.ResolveBadgePosition(actionParameters);
            var badgePosition = renderStateIndex == 1
                ? configuredBadgePosition
                : BadgePosition.None;

            // Automatic badges use the clean inactive artwork as their base. Manual two-state
            // actions (BadgePosition.None) keep their authored active image unchanged.
            var imageStateIndex = badgePosition == BadgePosition.None ? renderStateIndex : 0;
            var baseImageBytes = hasDevice
                ? ProfileImageBadgeSynchronizer.GetCommandImageBytes(
                    this.DataFlow,
                    deviceId,
                    configuredBadgePosition,
                    imageStateIndex)
                : null;

            if (baseImageBytes == null && hasDevice)
            {
                // Populate the in-memory image cache if the host requested a tile before OnLoad
                // completed. We MUST pass the real Windows-default key (_lastDefaultKey) so that
                // the synchronizer correctly marks isCurrentDevice for every button, not just the
                // one currently being rendered.  Using deviceId here would treat the rendered
                // button as if it were active, corrupting the cache for all other buttons.
                var currentDefaultForSync = this._lastDefaultKey ?? deviceId;
                ProfileImageBadgeSynchronizer.Synchronize(this.DataFlow, currentDefaultForSync);
                baseImageBytes = ProfileImageBadgeSynchronizer.GetCommandImageBytes(
                    this.DataFlow,
                    deviceId,
                    configuredBadgePosition,
                    imageStateIndex);
            }

            var baseImage = baseImageBytes == null
                ? base.GetCommandImage(actionParameters, imageWidth, imageHeight)
                : null;

            return AudioDeviceSwitchImage.BuildCommandButtonImage(
                String.Empty,
                renderStateIndex,
                baseImage,
                baseImageBytes,
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
        Int32 stateIndex) => String.Empty;

    // ── Helpers ──────────────────────────────────────────────────────────────

    public void SetDeviceState(ActionEditorActionParameters actionParameters, Int32 stateIndex)
    {
        try
        {
            this.SetCurrentState(actionParameters, stateIndex);
        }
        catch (Exception ex)
        {
            PluginLog.Warning(ex, $"Failed to set device state to {stateIndex}");
        }
    }

    private Boolean SynchronizeActiveDevice(String currentKey)
    {
        if (String.IsNullOrEmpty(currentKey))
        {
            return false;
        }

        this._lastDefaultKey = currentKey;

        // Synchronize saved profile action states. Button art and Icon Editor templates remain
        // owned by the SDK/editor.
        var synchronized = ProfileImageBadgeSynchronizer.Synchronize(
            this.DataFlow,
            currentKey,
            onDeviceMatched: (actionParams, isCurrent) =>
            {
                this.SetDeviceState(actionParams, isCurrent ? 1 : 0);
            });

        // Also update via the real Loupedeck-internal ActionEditorActionParameters objects
        // (captured from GetCommandImage callbacks). This guarantees the software UI badge
        // updates correctly even if the synthesized params above produced a different hash.
        this.ApplyCachedStateUpdates(currentKey);

        this.TryNotifyImageChanged();
        return synchronized;
    }

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
            AudioSwitcherPlugin.NotifySoftwareUiImagesChanged();
            Volatile.Write(ref this._imageRefreshPending, 0);
            return true;
        }
        catch (NullReferenceException)
        {
            // SDK has no image-change subscriber yet while the profile is still loading.
            Volatile.Write(ref this._imageRefreshPending, 1);
            return false;
        }
    }

    private BadgePosition ResolveBadgePosition(ActionEditorActionParameters actionParameters)
    {
        if (actionParameters != null && actionParameters.TryGetString(LayoutControlName, out var lv))
        {
            return AudioDeviceSwitchImage.ParseBadgePosition(lv);
        }

        return BadgePosition.TopRight;
    }
}
