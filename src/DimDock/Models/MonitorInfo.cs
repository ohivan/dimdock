using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DimDock.Models;

public sealed class MonitorInfo : INotifyPropertyChanged
{
    private int _brightness;
    private string _displayName;

    public MonitorInfo(
        string id,
        string displayName,
        string deviceName,
        bool isPrimary,
        int brightness,
        uint minRaw,
        uint maxRaw,
        bool useVcpFallback)
    {
        Id = id;
        _displayName = displayName;
        DeviceName = deviceName;
        IsPrimary = isPrimary;
        _brightness = Math.Clamp(brightness, 0, 100);
        MinRaw = minRaw;
        MaxRaw = maxRaw;
        UseVcpFallback = useVcpFallback;
    }

    public string Id { get; }
    public string DeviceName { get; }
    public bool IsPrimary { get; }
    public uint MinRaw { get; }
    public uint MaxRaw { get; }
    public bool UseVcpFallback { get; }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName == value) return;
            _displayName = value;
            OnPropertyChanged();
        }
    }

    public int Brightness
    {
        get => _brightness;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (_brightness == clamped) return;
            _brightness = clamped;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
