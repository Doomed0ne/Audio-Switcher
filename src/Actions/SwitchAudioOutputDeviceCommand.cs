namespace Loupedeck.AudioSwitcherPlugin;

public class SwitchAudioOutputDeviceCommand : AudioDeviceSwitchDynamicCommandBase
{
    public SwitchAudioOutputDeviceCommand()
        : base("Audio Output Devices", "Switch to a specific audio output device")
    {
    }

    protected override EDataFlow DataFlow => EDataFlow.Render;

    protected override string ShortLabel => "OUTPUT";
}
