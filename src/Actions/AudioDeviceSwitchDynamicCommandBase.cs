namespace Loupedeck.AudioSwitcherPlugin;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

public abstract class AudioDeviceSwitchDynamicCommandBase : PluginDynamicCommand
{
    private readonly Timer _refreshTimer;
    private readonly Dictionary<String, AudioDeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Object _devicesLock = new();
    private readonly ConcurrentDictionary<String, Byte> _loggedImageParameters = new(StringComparer.OrdinalIgnoreCase);
    private String _deviceSignature = "";
    private volatile String _currentDefaultKey;
    private Int32 _imageRefreshPending = 1;
    private Int32 _imageRefreshWarningLogged;

    protected AudioDeviceSwitchDynamicCommandBase(String displayName, String description)
        : base(displayName, description, "Audio")
    {
        this._refreshTimer = new Timer(this.OnTimerTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    protected abstract EDataFlow DataFlow { get; }

    protected abstract String ShortLabel { get; }

    protected override Boolean OnLoad()
    {
        try
        {
            this.RefreshDevices(forceUiRefresh: true);
        }
        catch (Exception ex)
        {
            // A transient service/profile startup error must not prevent future device
            // refreshes. The timer retries initialization and the pending image redraw.
            PluginLog.Warning(ex, $"{this.DisplayName} initial device refresh failed; the timer will retry.");
        }
        finally
        {
            this._refreshTimer.Change(250, 1000);
        }

        return true;
    }

    protected override Boolean OnUnload()
    {
        this._refreshTimer.Change(Timeout.Infinite, Timeout.Infinite);
        return true;
    }

    protected override void RunCommand(String actionParameter)
    {
        var deviceKey = this.ResolveDeviceKey(actionParameter);
        if (String.IsNullOrEmpty(deviceKey))
        {
            return;
        }

        if (AudioDeviceManager.SetDefault(deviceKey))
        {
            this._currentDefaultKey = deviceKey;
            this.TryNotifyImageChanged();
        }
        else
        {
            PluginLog.Warning($"Dynamic switch failed for {this.DisplayName}: {deviceKey}");
        }
    }

    protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize)
    {
        var deviceKey = this.ResolveDeviceKey(actionParameter);
        if (String.IsNullOrEmpty(deviceKey))
        {
            return base.GetCommandDisplayName(actionParameter, imageSize);
        }

        lock (this._devicesLock)
        {
            return this._devices.TryGetValue(deviceKey, out var device)
                ? device.Name
                : base.GetCommandDisplayName(actionParameter, imageSize);
        }
    }

    protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
    {
        var deviceKey = this.ResolveDeviceKey(actionParameter);
        if (String.IsNullOrEmpty(deviceKey))
        {
            return null;
        }

        AudioDeviceInfo? device = null;
        lock (this._devicesLock)
        {
            if (this._devices.TryGetValue(deviceKey, out var cachedDevice))
            {
                device = cachedDevice;
            }
        }

        var isCurrent = String.Equals(deviceKey, this._currentDefaultKey, StringComparison.OrdinalIgnoreCase);
        var deviceName = device?.Name ?? deviceKey;

        // Log each distinct parameter once. This proves whether the host requested an
        // image for the tested action and whether its key matched the device cache.
        var firstImageRequest = this._loggedImageParameters.TryAdd(actionParameter ?? "<empty>", 0);
        if (firstImageRequest)
        {
            PluginLog.Info(
                $"{this.DisplayName} image callback: parameter='{actionParameter}', resolvedKey='{deviceKey}', " +
                $"deviceCached={device.HasValue}, defaultKey='{this._currentDefaultKey}', active={isCurrent}.");
            if (!device.HasValue)
            {
                // Keep a valid action visible through startup/device-list races. The raw key
                // is only a temporary label until the next timer refresh resolves its name.
                PluginLog.Warning($"{this.DisplayName} image parameter '{actionParameter}' is not in the current device cache; rendering a fallback tile.");
            }
        }

        return this.BuildDeviceImage(deviceKey, deviceName, isCurrent, imageSize);
    }

    private void OnTimerTick(Object state)
    {
        try
        {
            this.RefreshDevices(forceUiRefresh: false);

            if (Volatile.Read(ref this._imageRefreshPending) != 0)
            {
                this.TryNotifyImageChanged();
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, $"Dynamic audio device polling failed: {ex.Message}");
        }
    }

    private void RefreshDevices(Boolean forceUiRefresh)
    {
        var devices = AudioDeviceManager.GetDevices(this.DataFlow)
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var currentDefaultKey = this.ReadDefaultDeviceKey();
        var signature = String.Join("|", devices.Select(d => $"{d.Key}:{d.Name}"));
        var deviceListChanged = !String.Equals(this._deviceSignature, signature, StringComparison.Ordinal);
        var defaultChanged = !String.Equals(this._currentDefaultKey, currentDefaultKey, StringComparison.OrdinalIgnoreCase);

        if (deviceListChanged)
        {
            this.RemoveAllParameters();
            lock (this._devicesLock)
            {
                this._devices.Clear();
                foreach (var device in devices)
                {
                    this._devices[device.Key] = device;
                }
            }

            foreach (var device in devices)
            {
                this.AddParameter(device.Key, device.Name, this.DisplayName);
            }

            this._deviceSignature = signature;
            this.ParametersChanged();
        }

        this._currentDefaultKey = currentDefaultKey;

        if (forceUiRefresh || deviceListChanged || defaultChanged)
        {
            this.TryNotifyImageChanged();
        }
    }

    private String ReadDefaultDeviceKey() => AudioDeviceManager.GetDefaultDevice(this.DataFlow)?.Key;

    private Boolean TryNotifyImageChanged()
    {
        try
        {
            // The SDK explicitly documents an unscoped redraw for buttons currently
            // shown on the device. Use that overload so the host's serialized parameter
            // format cannot cause a targeted invalidation to miss the action.
            this.ActionImageChanged();
            Volatile.Write(ref this._imageRefreshPending, 0);
            Interlocked.Exchange(ref this._imageRefreshWarningLogged, 0);
            return true;
        }
        catch (NullReferenceException ex)
        {
            Volatile.Write(ref this._imageRefreshPending, 1);
            if (Interlocked.Exchange(ref this._imageRefreshWarningLogged, 1) == 0)
            {
                PluginLog.Warning(ex, $"{this.DisplayName} image redraw is not available yet; retrying after startup.");
            }
            return false;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref this._imageRefreshPending, 1);
            if (Interlocked.Exchange(ref this._imageRefreshWarningLogged, 1) == 0)
            {
                PluginLog.Warning(ex, $"{this.DisplayName} image redraw failed; retrying on the next device refresh.");
            }
            return false;
        }
    }

    private String ResolveDeviceKey(String actionParameter)
    {
        if (String.IsNullOrEmpty(actionParameter))
        {
            return null;
        }

        lock (this._devicesLock)
        {
            if (this._devices.ContainsKey(actionParameter))
            {
                return actionParameter;
            }

            foreach (var key in this._devices.Keys)
            {
                if (actionParameter.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return key;
                }
            }
        }

        return actionParameter;
    }

    private BitmapImage BuildDeviceImage(String deviceKey, String deviceName, Boolean isCurrent, PluginImageSize imageSize)
    {
        using var builder = new BitmapBuilder(imageSize);
        builder.Clear(new BitmapColor(10, 10, 12));

        var title = String.IsNullOrEmpty(deviceName) ? deviceKey : deviceName;
        var shortTitle = title.Length > 22 ? title.Substring(0, 20) + ".." : title;

        builder.DrawText(this.ShortLabel, 0, 7, builder.Width, 22, new BitmapColor(180, 220, 255), 13, 15, 0);
        builder.DrawText(shortTitle, 4, 28, builder.Width - 8, builder.Height - 32, BitmapColor.White, 11, 13, 0);

        if (isCurrent)
        {
            AudioDeviceSwitchImage.DrawCheckBadge(builder);
        }

        return builder.ToImage();
    }
}
