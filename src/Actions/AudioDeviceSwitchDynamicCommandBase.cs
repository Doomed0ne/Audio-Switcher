namespace Loupedeck.AudioSwitcherPlugin;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

public abstract class AudioDeviceSwitchDynamicCommandBase : PluginDynamicCommand
{
    private readonly Timer _refreshTimer;
    private readonly Dictionary<String, AudioDeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);
    private String _deviceSignature = "";
    private String _currentDefaultKey;

    protected AudioDeviceSwitchDynamicCommandBase(String displayName, String description)
        : base(displayName, description, "Audio")
    {
        this._refreshTimer = new Timer(this.OnTimerTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    protected abstract EDataFlow DataFlow { get; }

    protected abstract String ShortLabel { get; }

    protected override Boolean OnLoad()
    {
        this.RefreshDevices(forceUiRefresh: true);
        this._refreshTimer.Change(250, 1000);
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

        var oldDefaultKey = this._currentDefaultKey;
        if (AudioDeviceManager.SetDefault(deviceKey))
        {
            this._currentDefaultKey = deviceKey;
            this.RefreshDeviceImages(oldDefaultKey, deviceKey);
        }
        else
        {
            PluginLog.Warning($"Dynamic switch failed for {this.DisplayName}: {deviceKey}");
        }
    }

    protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize)
    {
        var deviceKey = this.ResolveDeviceKey(actionParameter);
        return !String.IsNullOrEmpty(deviceKey) && this._devices.TryGetValue(deviceKey, out var device)
            ? device.Name
            : base.GetCommandDisplayName(actionParameter, imageSize);
    }

    protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
    {
        var deviceKey = this.ResolveDeviceKey(actionParameter);
        if (String.IsNullOrEmpty(deviceKey))
        {
            return null;
        }

        if (!this._devices.TryGetValue(deviceKey, out var device))
        {
            device = AudioDeviceManager.GetDevices(this.DataFlow)
                .FirstOrDefault(d => String.Equals(d.Key, deviceKey, StringComparison.OrdinalIgnoreCase));
        }

        var isCurrent = String.Equals(
            deviceKey,
            this.ReadDefaultDeviceKey(),
            StringComparison.OrdinalIgnoreCase);

        return this.BuildDeviceImage(deviceKey, device.Name, isCurrent, imageSize);
    }

    private void OnTimerTick(Object state)
    {
        try
        {
            this.RefreshDevices(forceUiRefresh: false);
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
        var oldDefaultKey = this._currentDefaultKey;

        if (deviceListChanged)
        {
            this.RemoveAllParameters();
            this._devices.Clear();

            foreach (var device in devices)
            {
                this._devices[device.Key] = device;
                this.AddParameter(device.Key, device.Name, this.DisplayName);
            }

            this._deviceSignature = signature;
            this.ParametersChanged();
        }

        this._currentDefaultKey = currentDefaultKey;

        if (forceUiRefresh || deviceListChanged)
        {
            this.ActionImageChanged();
        }
        else if (defaultChanged)
        {
            this.RefreshDeviceImages(oldDefaultKey, currentDefaultKey);
        }
    }

    private String ReadDefaultDeviceKey() => AudioDeviceManager.GetDefaultDevice(this.DataFlow)?.Key;

    private void RefreshDeviceImages(String oldDefaultKey, String newDefaultKey)
    {
        if (!String.IsNullOrEmpty(oldDefaultKey))
        {
            this.ActionImageChanged(oldDefaultKey);
        }

        if (!String.IsNullOrEmpty(newDefaultKey))
        {
            this.ActionImageChanged(newDefaultKey);
        }
    }

    private String ResolveDeviceKey(String actionParameter)
    {
        if (String.IsNullOrEmpty(actionParameter))
        {
            return null;
        }

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
