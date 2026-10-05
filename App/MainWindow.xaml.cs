using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using MultiHeadsetAudioSharing.Core;

namespace MultiHeadsetAudioSharing.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DispatcherTimer _timer;
    private AudioDeviceManager? _devices;
    private SessionCoordinator? _session;
    private Task? _operationTask;
    private SessionStatus? _status;
    private string? _sourceId;
    private string _notification = "";
    private bool _running;
    private bool _busy;
    private bool _closing;
    private bool _allowClose;
    private bool _refreshingDevices;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _timer.Tick += Timer_Tick;
    }

    public ObservableCollection<AudioDeviceInfo> SourceDevices { get; } = [];
    public ObservableCollection<DeviceRow> Devices { get; } = [];

    public string? SourceId
    {
        get => _sourceId;
        set
        {
            if (_refreshingDevices || !CanEditSelection || _sourceId == value) return;
            _sourceId = value;
            Notify();
            UpdateControls();
        }
    }

    public bool CanEditSelection => !_running && !_busy && !_closing;
    public bool CanRefresh => !_busy && !_closing;
    public bool CanStart => CanEditSelection && _session is not null &&
        Devices.Any(d => d.Id == _sourceId && d.IsActive) && SelectedCount is >= 2 and <= 5;
    public bool CanStop => _running && !_busy && !_closing;
    public int SelectedCount => Devices.Count(d => d.IsSelected && d.IsActive);
    public string SelectionSummary => $"Dipilih: {SelectedCount}/5 pendengar • Aktif dalam sesi: {ActiveCount}. " +
        "Pilih 2–5 endpoint aktif; sumber yang dicentang dihitung satu kali melalui Windows.";
    private int ActiveCount => _running && _status is not null
        ? _status.Outputs.Count + (_status.SourceIsListener ? 1 : 0) : 0;
    public string EmptyDevicesMessage => Devices.Any(d => d.IsActive) ? "" :
        "Tidak ada output audio aktif. Hubungkan headset/speaker melalui Windows, aktifkan perangkat, lalu tekan Segarkan perangkat.";
    public string SourceDescription
    {
        get
        {
            var source = Devices.FirstOrDefault(d => d.Id == _sourceId);
            return source is null ? "Belum ada sumber aktif yang dipilih." :
                $"Sumber: {source.Name}\nEndpoint: {source.Id}";
        }
    }
    public string RunState => _closing ? "Menutup dan melepas audio…" : _busy ? "Memproses…" :
        _running ? "Sedang berbagi" : "Berhenti";
    public double InputPeakPercent => Math.Clamp((_status?.InputPeak ?? 0) * 100d, 0, 100);
    public string StatusMessage => _status?.Message ?? "Siap. Pilih sumber dan perangkat pendengar untuk memulai.";
    public string Notification => _notification;

    private void Window_Loaded(object sender, RoutedEventArgs e) => InitializeEngine();

    private void InitializeEngine()
    {
        if (_session is not null || _closing) return;
        AudioDeviceManager? devices = null;
        try
        {
            devices = new AudioDeviceManager();
            var session = new SessionCoordinator(devices);
            _devices = devices;
            _session = session;
            devices.DevicesChanged += Devices_Changed;
            RefreshDevices();
            PollStatus();
            _timer.Start();
        }
        catch (Exception ex)
        {
            if (_session is null) devices?.Dispose();
            SetNotification($"Audio Windows tidak dapat dibuka: {ex.Message}. Coba Segarkan perangkat.");
            UpdateControls();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRefresh) return;
        SetNotification("");
        if (_session is null) InitializeEngine();
        else RefreshDevices();
    }

    private void Devices_Changed(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_closing) RefreshDevices();
        }));
    }

    private void RefreshDevices()
    {
        if (_devices is null || _closing) return;
        try
        {
            var snapshot = _devices.GetDevices();
            var ids = snapshot.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var device in snapshot)
            {
                var row = Devices.FirstOrDefault(d => d.Id == device.Id);
                if (row is null) Devices.Add(new DeviceRow(device, DeviceRow_Changed));
                else row.UpdateDevice(device);
            }
            foreach (var missing in Devices.Where(d => !ids.Contains(d.Id)).ToArray())
            {
                if (_running || _busy)
                    missing.UpdateDevice(new AudioDeviceInfo(missing.Id, missing.Name, false, "Terputus"));
                else
                    Devices.Remove(missing);
            }
            if (CanEditSelection)
            {
                foreach (var row in Devices.Where(d => !d.IsActive)) row.IsSelected = false;
                _refreshingDevices = true;
                try
                {
                    var activeSources = snapshot.Where(d => d.IsActive).ToArray();
                    foreach (var missing in SourceDevices.Where(d => !activeSources.Any(a => a.Id == d.Id)).ToArray())
                        SourceDevices.Remove(missing);
                    foreach (var device in activeSources)
                    {
                        var existing = SourceDevices.FirstOrDefault(d => d.Id == device.Id);
                        if (existing is null) SourceDevices.Add(device);
                        else if (existing != device) SourceDevices[SourceDevices.IndexOf(existing)] = device;
                    }
                    if (!SourceDevices.Any(d => d.Id == _sourceId))
                    {
                        var defaultId = _devices.GetDefaultSourceId();
                        _sourceId = SourceDevices.FirstOrDefault(d => d.Id == defaultId)?.Id ??
                            SourceDevices.FirstOrDefault()?.Id;
                    }
                    SourceSelector.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
                        SourceDevices.FirstOrDefault(d => d.Id == _sourceId));
                }
                finally
                {
                    _refreshingDevices = false;
                }
                Notify(nameof(SourceId));
            }
            UpdateControls();
        }
        catch (Exception ex)
        {
            SetNotification($"Daftar perangkat tidak dapat diperbarui: {ex.Message}");
        }
    }

    private void DeviceRow_Changed(DeviceRow row, bool volumeChanged)
    {
        if (volumeChanged && _running && !row.IsSource && _session is not null)
        {
            try
            {
                _session.SetGain(row.Id, (float)(row.Volume / 100));
            }
            catch (Exception ex)
            {
                SetNotification($"Volume {row.Name} tidak dapat diubah: {ex.Message}");
            }
        }
        if (!volumeChanged) UpdateControls();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStart || _session is null || _sourceId is null) return;
        var sourceId = _sourceId;
        var nativeListener = Devices.Any(d => d.Id == sourceId && d.IsSelected);
        var outputs = Devices.Where(d => d.IsSelected && d.IsActive && d.Id != sourceId)
            .Select(d => new OutputSelection(d.Id, (float)(d.Volume / 100))).ToArray();
        _busy = true;
        SetNotification("");
        UpdateControls();
        try
        {
            _operationTask = _session.StartAsync(sourceId, nativeListener, outputs);
            await _operationTask;
        }
        catch (Exception ex)
        {
            if (!_closing) SetNotification($"Sesi gagal dimulai: {ex.Message}");
        }
        finally
        {
            _operationTask = null;
            _busy = false;
            if (!_closing)
            {
                PollStatus();
                if (!_running) RefreshDevices();
                UpdateControls();
            }
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStop || _session is null) return;
        _busy = true;
        SetNotification("");
        UpdateControls();
        try
        {
            _operationTask = _session.StopAsync();
            await _operationTask;
        }
        catch (Exception ex)
        {
            if (!_closing) SetNotification($"Sesi tidak dapat dihentikan: {ex.Message}");
        }
        finally
        {
            _operationTask = null;
            _busy = false;
            if (!_closing)
            {
                PollStatus();
                RefreshDevices();
                UpdateControls();
            }
        }
    }

    private void Timer_Tick(object? sender, EventArgs e) => PollStatus();

    private void PollStatus()
    {
        if (_session is null || _closing) return;
        try
        {
            var wasRunning = _running;
            _status = _session.GetStatus();
            _running = _status.IsRunning;
            var outputs = _status.Outputs.ToDictionary(o => o.DeviceId, StringComparer.Ordinal);
            foreach (var row in Devices)
            {
                var joined = outputs.TryGetValue(row.Id, out var output);
                row.UpdateContext(row.Id == _sourceId, !CanEditSelection, _running, _closing, joined);
                if (joined && output is not null)
                    row.UpdatePlayback($"Memutar • Buffer: {output.BufferedMilliseconds} ms • " +
                        $"Underrun: {output.UnderrunFrames:N0} frame • Dibuang: {output.DroppedFrames:N0} frame");
                else if (_running && row.Id == _status.SourceId)
                    row.UpdatePlayback(_status.SourceIsListener ? "Pendengar asli Windows; volume dikelola Windows." :
                        "Hanya sumber capture; tetap dapat terdengar melalui Windows, tidak dihitung sebagai pendengar.");
                else if (_running && row.IsSelected)
                    row.UpdatePlayback("Tidak lagi dalam sesi. Sambungkan kembali, lalu berhenti dan pilih ulang untuk bergabung.");
                else
                    row.UpdatePlayback(row.IsActive ? "Belum berjalan." : "Perangkat tidak tersedia.");
            }
            Notify(nameof(InputPeakPercent));
            Notify(nameof(StatusMessage));
            Notify(nameof(SelectionSummary));
            if (wasRunning != _running)
            {
                if (!_running && !_busy) RefreshDevices();
                UpdateControls();
            }
        }
        catch (Exception ex)
        {
            SetNotification($"Status audio tidak dapat dibaca: {ex.Message}");
        }
    }

    private void UpdateControls()
    {
        foreach (var row in Devices)
        {
            var joined = _status?.Outputs.Any(o => o.DeviceId == row.Id) ?? false;
            row.UpdateContext(row.Id == _sourceId, !CanEditSelection, _running, _closing, joined);
        }
        Notify(nameof(CanEditSelection));
        Notify(nameof(CanRefresh));
        Notify(nameof(CanStart));
        Notify(nameof(CanStop));
        Notify(nameof(SelectionSummary));
        Notify(nameof(SourceDescription));
        Notify(nameof(EmptyDevicesMessage));
        Notify(nameof(RunState));
    }

    private void SetNotification(string message)
    {
        if (_notification == message) return;
        _notification = message;
        Notify(nameof(Notification));
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _busy = true;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        if (_devices is not null) _devices.DevicesChanged -= Devices_Changed;
        UpdateControls();
        Exception? closeError = null;
        try
        {
            if (_operationTask is not null)
            {
                try { await _operationTask; }
                catch (Exception ex) { closeError = ex; }
            }
            if (_session is not null) await _session.DisposeAsync();
        }
        catch (Exception ex)
        {
            closeError = ex;
        }
        finally
        {
            try { _devices?.Dispose(); }
            catch (Exception ex) { closeError ??= ex; }
            if (closeError is not null)
                MessageBox.Show(this, $"Windows melaporkan kesalahan saat melepas audio: {closeError.Message}",
                    "Pelepasan audio", MessageBoxButton.OK, MessageBoxImage.Warning);
            _allowClose = true;
            Close();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
