using NAudio.Wave;
using WhisperSTT.Core.Models;
using WhisperSTT.Core.Services;

namespace WhisperSTT.App.Services;

public sealed class AudioInputDeviceService : IAudioInputDeviceService
{
    public IReadOnlyList<AudioInputDeviceOption> GetAvailableDevices()
    {
        var devices = new List<AudioInputDeviceOption>();
        for (var deviceNumber = 0; deviceNumber < WaveInEvent.DeviceCount; deviceNumber++)
        {
            var capabilities = WaveInEvent.GetCapabilities(deviceNumber);
            var productName = string.IsNullOrWhiteSpace(capabilities.ProductName)
                ? $"Input device {deviceNumber}"
                : capabilities.ProductName.Trim();
            devices.Add(new AudioInputDeviceOption(deviceNumber, $"{deviceNumber}: {productName}"));
        }

        return devices;
    }
}
