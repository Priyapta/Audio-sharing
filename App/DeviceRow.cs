using System.ComponentModel;
using System.Runtime.CompilerServices;
using MultiHeadsetAudioSharing.Core;

namespace MultiHeadsetAudioSharing.App;

public sealed class DeviceRow : INotifyPropertyChanged
{
    private readonly Action<DeviceRow, bool> _changed;
    private string _name;
    private string _state;
    private bool _isActive;
    private bool _isSelected;
    private bool _isSource;
    private bool _canSelect;
    private bool _canAdjustVolume;
    private double _volume = 100;
    private string _playbackDescription = "Belum berjalan.";

    public DeviceRow(AudioDeviceInfo device, Action<DeviceRow, bool> changed)
    {
        Id = device.Id;
        _name = device.Name;
        _state = device.State;
        _isActive = device.IsActive;
        _changed = changed;
    }

    public string Id { get; }
    public string Name => _name;
    public bool IsActive => _isActive;
    public bool IsSource => _isSource;
    public bool CanSelect => _canSelect;
    public bool CanAdjustVolume => _canAdjustVolume;
    public string DeviceDescription => $"{(_isSource ? "SUMBER • " : "")}{(_isActive ? "Aktif" : "Tidak tersedia")} • {_state}\nID: {Id}";
    public string PlaybackDescription => _playbackDescription;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Notify();
            _changed(this, false);
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (_volume == clamped) return;
            _volume = clamped;
            Notify();
            _changed(this, true);
        }
    }

    public void UpdateDevice(AudioDeviceInfo device)
    {
        if (_name != device.Name)
        {
            _name = device.Name;
            Notify(nameof(Name));
        }
        if (_isActive == device.IsActive && _state == device.State) return;
        _isActive = device.IsActive;
        _state = device.State;
        Notify(nameof(IsActive));
        Notify(nameof(DeviceDescription));
    }

    public void UpdateContext(bool source, bool locked, bool running, bool closing, bool joined)
    {
        if (_isSource != source)
        {
            _isSource = source;
            Notify(nameof(IsSource));
            Notify(nameof(DeviceDescription));
        }
        var canSelect = _isActive && !locked;
        var canAdjust = _isActive && !source && !closing && (!locked || running && joined);
        if (_canSelect != canSelect)
        {
            _canSelect = canSelect;
            Notify(nameof(CanSelect));
        }
        if (_canAdjustVolume != canAdjust)
        {
            _canAdjustVolume = canAdjust;
            Notify(nameof(CanAdjustVolume));
        }
    }

    public void UpdatePlayback(string description)
    {
        if (_playbackDescription == description) return;
        _playbackDescription = description;
        Notify(nameof(PlaybackDescription));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
