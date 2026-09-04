namespace Loupedeck.AudioSwitcherPlugin;

public class SwitchAudioInputDeviceCommand : AudioDeviceSwitchDynamicCommandBase
{
    public SwitchAudioInputDeviceCommand()
        : base("Audio Input Devices", "Switch to a specific audio input device")
    {
    }

    protected override EDataFlow DataFlow => EDataFlow.Capture;

    protected override string ShortLabel => "INPUT";
}
