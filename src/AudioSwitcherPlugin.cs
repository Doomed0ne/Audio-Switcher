namespace Loupedeck.AudioSwitcherPlugin;

using System;

public class AudioSwitcherPlugin : Plugin
{
    public override Boolean UsesApplicationApiOnly => true;
    public override Boolean HasNoApplication => true;

    public static AudioSwitcherPlugin Instance { get; private set; }

    public AudioSwitcherPlugin()
    {
        Instance = this;
        PluginLog.Init(this.Log);
    }

    /// <summary>
    /// Notifies LoupedeckService that plugin images or states have changed.
    /// Loupedeck's ActionImageChanged() only updates hardware LCD devices (DeviceState70).
    /// Calling OnPluginStatusChanged triggers ConfigurationWindowMessageHandler.SendProfileActionImagesChangedEvent,
    /// which broadcasts ProfileActionImagesChangedEventArgs over WebSocket to the Electron software UI,
    /// causing the desktop app to refetch action images and stay in sync with the hardware.
    /// </summary>
    public static void NotifySoftwareUiImagesChanged()
    {
        try
        {
            Instance?.OnPluginStatusChanged(Loupedeck.PluginStatus.Normal, String.Empty);
        }
        catch (Exception ex)
        {
            PluginLog.Warning(ex, "Failed to notify software UI of image change.");
        }
    }

    public override void Load()
    {
        base.Load();
        PluginLog.Info("AudioSwitcherPlugin loaded. Initializing active Windows audio endpoints on startup.");

        try
        {
            var defaultOutput = AudioDeviceManager.GetDefaultDevice(EDataFlow.Render);
            if (defaultOutput is { } output)
            {
                PluginLog.Info($"Startup active render endpoint: {output.Name} ({output.Key})");
            }

            var defaultInput = AudioDeviceManager.GetDefaultDevice(EDataFlow.Capture);
            if (defaultInput is { } input)
            {
                PluginLog.Info($"Startup active capture endpoint: {input.Name} ({input.Key})");
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, $"Failed to query startup audio devices: {ex.Message}");
        }
    }
}
