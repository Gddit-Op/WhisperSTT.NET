using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Enums;
using WhisperSTT.Core.Models;
using WhisperSTT.Core.Services;

namespace WhisperSTT.App.Services;

public sealed class AudioInputDeviceService : IAudioInputDeviceService
{
    public IReadOnlyList<AudioInputDeviceOption> GetAvailableDevices()
    {
        using var audioEngine = new MiniAudioEngine(Array.Empty<MiniAudioBackend>());
        var devices = new List<AudioInputDeviceOption>();
        for (var deviceNumber = 0; deviceNumber < audioEngine.CaptureDevices.Length; deviceNumber++)
        {
            var deviceInfo = audioEngine.CaptureDevices[deviceNumber];
            var productName = string.IsNullOrWhiteSpace(deviceInfo.Name)
                ? $"Input device {deviceNumber}"
                : deviceInfo.Name.Trim();
            devices.Add(new AudioInputDeviceOption(deviceNumber, $"{deviceNumber}: {productName}"));
        }

        return devices;
    }
}
