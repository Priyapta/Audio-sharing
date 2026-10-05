using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiHeadsetAudioSharing.Core;

public record OutputSelection(string DeviceId, float Gain);
public record OutputStatus(string DeviceId, string Name, float Gain, int BufferedMilliseconds,
    long UnderrunFrames, long DroppedFrames);
public record SessionStatus(bool IsRunning, string? SourceId, bool SourceIsListener, float InputPeak,
    IReadOnlyList<OutputStatus> Outputs, string Message);

public sealed class SessionCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);
    private readonly AudioDeviceManager _devices;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _stateGate = new();
    private Session? _current;
    private Session? _starting;
    private string _message = "Siap. Pilih sumber dan 2–5 perangkat pendengar.";
    private bool _disposeRequested;
    private Task? _disposeTask;
    private int _refreshQueued;

    public SessionCoordinator(AudioDeviceManager devices)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _devices.DevicesChanged += OnDevicesChanged;
        _devices.EndpointUnavailable += OnEndpointUnavailable;
    }

    public SessionStatus GetStatus()
    {
        lock (_stateGate)
        {
            var session = _current;
            if (session is null)
                return new SessionStatus(false, null, false, 0, Array.Empty<OutputStatus>(), _message);

            var outputs = new OutputStatus[session.Outputs.Count];
            var index = 0;
            foreach (var output in session.Outputs.Values)
            {
                outputs[index++] = new OutputStatus(output.Id, output.Name, output.Provider.Gain,
                    output.Provider.BufferedMilliseconds, output.Provider.UnderrunFrames,
                    output.Provider.DroppedFrames);
            }
            var peak = session.Buffers.InputPeak;
            return new SessionStatus(true, session.SourceId, session.SourceIsListener, peak, outputs, _message);
        }
    }

    public Task StartAsync(string sourceId, bool sourceIsListener, IReadOnlyList<OutputSelection> outputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(outputs);
        // The caller can change its selection list as soon as this method returns.
        var selections = outputs.ToArray();
        var turn = _operations.WaitAsync();
        return Task.Run(async () =>
        {
            await turn.ConfigureAwait(false);
            try { await StartCoreAsync(sourceId, sourceIsListener, selections).ConfigureAwait(false); }
            finally { _operations.Release(); }
        });
    }

    public Task StopAsync()
    {
        var turn = _operations.WaitAsync();
        return Task.Run(async () =>
        {
            await turn.ConfigureAwait(false);
            try { await StopCoreAsync("Sesi dihentikan.").ConfigureAwait(false); }
            finally { _operations.Release(); }
        });
    }

    public void SetGain(string outputId, float gain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputId);
        ValidateGain(gain);
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            var session = _current ?? throw new InvalidOperationException("Tidak ada sesi yang berjalan.");
            if (StringComparer.Ordinal.Equals(outputId, session.SourceId))
                throw new InvalidOperationException("Volume sumber dikelola Windows, bukan aplikasi ini.");
            if (!session.Outputs.TryGetValue(outputId, out var output))
                throw new InvalidOperationException("Output tidak lagi mengikuti sesi ini. Hentikan sesi untuk memilih ulang.");
            // Gain belongs to the samples for this reader, never an endpoint/session master volume.
            output.Provider.Gain = gain;
        }
    }

    private async Task StartCoreAsync(string sourceId, bool sourceIsListener, OutputSelection[] selections)
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            if (_current is not null)
                throw new InvalidOperationException("Hentikan sesi sebelum memulai sesi baru.");
            _message = "Menyiapkan semua endpoint audio…";
        }

        Session? session = null;
        var endpointDescription = $"sumber '{sourceId}'";
        try
        {
            ValidateSelections(sourceId, sourceIsListener, selections);
            var inventory = _devices.GetDevices().ToDictionary(device => device.Id, StringComparer.Ordinal);
            var source = RequireActiveDevice(inventory, sourceId);
            endpointDescription = $"sumber '{source.Name}'";
            var outputNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var selection in selections)
            {
                endpointDescription = $"output '{selection.DeviceId}'";
                outputNames.Add(selection.DeviceId, RequireActiveDevice(inventory, selection.DeviceId).Name);
            }
            session = new Session(sourceId, source.Name, sourceIsListener, outputNames);
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposeRequested, this);
                _starting = session;
            }

            endpointDescription = $"sumber '{source.Name}'";
            session.SourceDevice = _devices.OpenActiveDevice(sourceId);
            session.Recorder = new WasapiRecorderBuilder()
                .WithDevice(session.SourceDevice)
                .WithLoopbackCapture()
                .WithSharedMode()
                .WithFormat(session.CaptureFormat)
                .WithBufferLength(20)
                .Build();
            session.DataHandler = (data, _, _, _) =>
            {
                if (Volatile.Read(ref session.AcceptAudio) == 0)
                    return;
                // NAudio 3.1 replaces undefined Silent-flag packets with zero-filled spans.
                session.Buffers.Write(data);
            };
            session.RecordingStoppedHandler = (_, args) => OnRecordingStopped(session, args.Exception);
            session.Recorder.DataAvailable += session.DataHandler;
            session.Recorder.RecordingStopped += session.RecordingStoppedHandler;

            // No player starts until every player is constructed and Init has succeeded.
            foreach (var selection in selections)
            {
                var name = outputNames[selection.DeviceId];
                endpointDescription = $"output '{name}'";
                var provider = session.Buffers.AddOutput(selection.DeviceId, selection.Gain);
                var output = new OutputStream(selection.DeviceId, name, provider);
                session.Outputs.Add(output.Id, output);
                output.Device = _devices.OpenActiveDevice(output.Id);
                output.Player = new WasapiPlayerBuilder()
                    .WithDevice(output.Device)
                    .WithSharedMode()
                    .WithLatency(40)
                    .Build();
                output.StoppedHandler = (_, args) => OnPlaybackStopped(session, output, args.Exception);
                output.Player.PlaybackStopped += output.StoppedHandler;
                output.Player.Init(output);
            }
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposeRequested, this);
                if (session.StartupError is not null)
                    throw session.StartupError;
            }

            foreach (var output in session.Outputs.Values)
            {
                endpointDescription = $"output '{output.Name}'";
                output.Player!.Play();
            }
            endpointDescription = $"sumber '{source.Name}'";
            Volatile.Write(ref session.AcceptAudio, 1);
            session.Recorder.StartRecording();
            session.CaptureStartReturned = true;
            endpointDescription = "sesi audio";
            await WaitForStartupAsync(session).ConfigureAwait(false);

            // Recheck availability after opening the streams; do not accept a half-started session.
            var activeIds = _devices.GetDevices().Where(device => device.IsActive)
                .Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
            if (!activeIds.Contains(session.SourceId))
                throw new InvalidOperationException($"Sumber '{session.SourceName}' terputus saat memulai.");
            foreach (var output in session.Outputs.Values)
            {
                if (!activeIds.Contains(output.Id))
                    throw new InvalidOperationException($"Output '{output.Name}' terputus saat memulai.");
            }
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposeRequested, this);
                if (session.StartupError is not null)
                    throw session.StartupError;
                _starting = null;
                _current = session;
                _message = RunningMessage(session);
            }
        }
        catch (Exception exception)
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_starting, session))
                    _starting = null;
            }
            var cleanupError = session is null ? null : await DisposeSessionAsync(session).ConfigureAwait(false);
            var message = $"Gagal memulai {endpointDescription}: {exception.Message}";
            if (cleanupError is not null)
                message += $" Pelepasan resource: {cleanupError}";
            lock (_stateGate)
                _message = message;
            throw new InvalidOperationException(message, exception);
        }
    }

    private async Task WaitForStartupAsync(Session session)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposeRequested, this);
                if (session.StartupError is not null)
                    throw session.StartupError;
            }
            var outputsStarted = true;
            foreach (var output in session.Outputs.Values)
            {
                if (!output.Started)
                {
                    outputsStarted = false;
                    break;
                }
            }
            if (session.Recorder!.CaptureState == CaptureState.Capturing && outputsStarted)
                return;
            if (Stopwatch.GetElapsedTime(startedAt) >= StartupTimeout)
            {
                var pending = session.Outputs.Values.Where(output => !output.Started)
                    .Select(output => $"output '{output.Name}'").ToList();
                if (session.Recorder.CaptureState != CaptureState.Capturing)
                    pending.Insert(0, $"sumber '{session.SourceName}'");
                throw new TimeoutException($"Endpoint belum berjalan setelah 5 detik: {string.Join(", ", pending)}.");
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private void OnRecordingStopped(Session session, Exception? exception)
    {
        var message = $"Capture sumber '{session.SourceName}' berhenti: " +
            (exception?.Message ?? "stream sumber tidak lagi tersedia.");
        lock (_stateGate)
        {
            if (ReferenceEquals(_starting, session))
            {
                session.StartupError ??= new InvalidOperationException(message, exception);
                return;
            }
            if (!ReferenceEquals(_current, session))
                return;
        }
        // Never dispose/join the recording thread inside its own stopped callback.
        QueueLifecycle(async () =>
        {
            if (IsCurrent(session))
                await StopCoreAsync(message).ConfigureAwait(false);
        });
    }

    private void OnPlaybackStopped(Session session, OutputStream output, Exception? exception)
    {
        var message = $"Output '{output.Name}' berhenti: " +
            (exception?.Message ?? "stream output tidak lagi tersedia.");
        lock (_stateGate)
        {
            if (ReferenceEquals(_starting, session))
            {
                session.StartupError ??= new InvalidOperationException(message, exception);
                return;
            }
            if (!ReferenceEquals(_current, session))
                return;
        }
        QueueLifecycle(() => RemoveOutputCoreAsync(session, output.Id, message));
    }

    private void OnEndpointUnavailable(string id)
    {
        Session? session;
        string message;
        lock (_stateGate)
        {
            session = _starting ?? _current;
            if (session is null)
                return;
            if (StringComparer.Ordinal.Equals(id, session.SourceId))
                message = $"Sumber '{session.SourceName}' terputus atau tidak aktif. Sesi dihentikan.";
            else if (session.OutputNames.TryGetValue(id, out var name))
                message = $"Output '{name}' terputus atau tidak aktif.";
            else
                return;
            if (ReferenceEquals(_starting, session))
            {
                session.StartupError ??= new InvalidOperationException(message);
                return;
            }
        }
        QueueLifecycle(async () =>
        {
            if (!IsCurrent(session))
                return;
            if (StringComparer.Ordinal.Equals(id, session.SourceId))
                await StopCoreAsync(message).ConfigureAwait(false);
            else
                await RemoveOutputCoreAsync(session, id, message).ConfigureAwait(false);
        });
    }

    private void OnDevicesChanged(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0)
            return;
        QueueLifecycle(async () =>
        {
            // Reset before enumeration: another change during the refresh deserves another pass.
            Volatile.Write(ref _refreshQueued, 0);
            Session? session;
            lock (_stateGate)
                session = _current;
            if (session is null)
                return;
            var activeIds = _devices.GetDevices().Where(device => device.IsActive)
                .Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
            if (!activeIds.Contains(session.SourceId))
            {
                await StopCoreAsync($"Sumber '{session.SourceName}' tidak tersedia. Sesi dihentikan.").ConfigureAwait(false);
                return;
            }
            // A default-device change does not reroute the pinned source or any player.
            foreach (var output in session.Outputs.Values.ToArray())
            {
                if (!activeIds.Contains(output.Id))
                    await RemoveOutputCoreAsync(session, output.Id,
                        $"Output '{output.Name}' tidak tersedia.").ConfigureAwait(false);
            }
        });
    }

    private void QueueLifecycle(Func<Task> operation)
    {
        var turn = _operations.WaitAsync();
        _ = Task.Run(async () =>
        {
            await turn.ConfigureAwait(false);
            try
            {
                lock (_stateGate)
                {
                    if (_disposeRequested)
                        return;
                }
                await operation().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await StopCoreAsync($"Sesi dihentikan karena kegagalan audio: {exception.Message}").ConfigureAwait(false);
            }
            finally { _operations.Release(); }
        });
    }

    private bool IsCurrent(Session session)
    {
        lock (_stateGate)
            return ReferenceEquals(_current, session);
    }

    private async Task RemoveOutputCoreAsync(Session session, string outputId, string message)
    {
        OutputStream? output;
        int listenerCount;
        lock (_stateGate)
        {
            if (!ReferenceEquals(_current, session) || !session.Outputs.Remove(outputId, out output))
                return;
            listenerCount = session.Outputs.Count + (session.SourceIsListener ? 1 : 0);
            _message = $"{message} {listenerCount} pendengar tersisa. Perangkat tidak bergabung ulang otomatis.";
        }
        session.Buffers.RemoveOutput(outputId);
        var cleanupError = await DisposeOutputAsync(output).ConfigureAwait(false);
        if (listenerCount == 0)
        {
            await StopCoreAsync($"{message} Tidak ada pendengar tersisa; sesi dihentikan." +
                (cleanupError is null ? "" : $" Pelepasan resource: {cleanupError}")).ConfigureAwait(false);
        }
        else if (cleanupError is not null)
        {
            lock (_stateGate)
                _message += $" Pelepasan resource: {cleanupError}";
        }
    }

    private async Task StopCoreAsync(string message)
    {
        Session? session;
        lock (_stateGate)
        {
            session = _current;
            _current = null;
            if (session is not null)
                _message = message;
        }
        if (session is null)
            return;
        var cleanupError = await DisposeSessionAsync(session).ConfigureAwait(false);
        if (cleanupError is not null)
        {
            lock (_stateGate)
                _message += $" Pelepasan resource: {cleanupError}";
        }
    }

    private static async Task<string?> DisposeSessionAsync(Session session)
    {
        Volatile.Write(ref session.AcceptAudio, 0);
        List<string>? errors = null;
        if (session.Recorder is { } recorder)
        {
            recorder.DataAvailable -= session.DataHandler;
            recorder.RecordingStopped -= session.RecordingStoppedHandler;
            // NAudio sets Capturing inside the new thread. Stopping while it is still
            // Starting could be overwritten by that transition and make its join hang.
            if (session.CaptureStartReturned)
            {
                while (recorder.CaptureState == CaptureState.Starting)
                    await Task.Delay(10).ConfigureAwait(false);
            }
            try { await recorder.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { (errors ??= []).Add($"sumber '{session.SourceName}': {exception.Message}"); }
            session.Recorder = null;
        }
        foreach (var output in session.Outputs.Values)
        {
            var error = await DisposeOutputAsync(output).ConfigureAwait(false);
            if (error is not null)
                (errors ??= []).Add(error);
        }
        session.Outputs.Clear();
        try { session.SourceDevice?.Dispose(); }
        catch (Exception exception) { (errors ??= []).Add($"endpoint sumber: {exception.Message}"); }
        session.SourceDevice = null;
        session.Buffers.Clear();
        return errors is null ? null : string.Join("; ", errors);
    }

    private static async Task<string?> DisposeOutputAsync(OutputStream output)
    {
        List<string>? errors = null;
        if (output.Player is { } player)
        {
            player.PlaybackStopped -= output.StoppedHandler;
            try { await player.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { (errors ??= []).Add(exception.Message); }
            output.Player = null;
        }
        try { output.Device?.Dispose(); }
        catch (Exception exception) { (errors ??= []).Add(exception.Message); }
        output.Device = null;
        return errors is null ? null : $"output '{output.Name}': {string.Join("; ", errors)}";
    }

    public ValueTask DisposeAsync()
    {
        Task task;
        lock (_stateGate)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);
            _disposeRequested = true;
            var turn = _operations.WaitAsync();
            task = _disposeTask = Task.Run(async () =>
            {
                await turn.ConfigureAwait(false);
                try { await StopCoreAsync("Aplikasi menutup sesi audio.").ConfigureAwait(false); }
                finally { _operations.Release(); }
            });
        }
        _devices.DevicesChanged -= OnDevicesChanged;
        _devices.EndpointUnavailable -= OnEndpointUnavailable;
        // The device manager is owned by the caller. Queued callbacks can still finish on the gate.
        return new ValueTask(task);
    }

    private static void ValidateSelections(string sourceId, bool sourceIsListener, OutputSelection[] selections)
    {
        var count = selections.Length + (sourceIsListener ? 1 : 0);
        if (count is < 2 or > 5)
            throw new ArgumentException("Pilih 2–5 pendengar; sumber hanya dihitung jika dipilih sebagai pendengar.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selection in selections)
        {
            ArgumentNullException.ThrowIfNull(selection);
            ArgumentException.ThrowIfNullOrWhiteSpace(selection.DeviceId);
            ValidateGain(selection.Gain);
            if (StringComparer.Ordinal.Equals(selection.DeviceId, sourceId))
                throw new ArgumentException("Sumber tidak boleh menerima playback ulang. Gunakan pilihan pendengar sumber.");
            if (!ids.Add(selection.DeviceId))
                throw new ArgumentException("Endpoint output yang sama tidak boleh dipilih dua kali.");
        }
    }

    private static void ValidateGain(float gain)
    {
        if (!float.IsFinite(gain) || gain is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(gain), "Gain harus bernilai 0–1 (0–100%).");
    }

    private static AudioDeviceInfo RequireActiveDevice(Dictionary<string, AudioDeviceInfo> inventory, string id)
    {
        if (!inventory.TryGetValue(id, out var device) || !device.IsActive)
            throw new InvalidOperationException($"Endpoint '{id}' tidak tersedia atau tidak aktif.");
        return device;
    }

    private static string RunningMessage(Session session)
    {
        var count = session.Outputs.Count + (session.SourceIsListener ? 1 : 0);
        return $"Berjalan: {count} pendengar. Sumber dipin ke '{session.SourceName}'. " +
            (session.SourceIsListener
                ? "Sumber didengar melalui playback asli Windows; volumenya dikelola Windows."
                : "Sumber tidak dihitung sebagai pendengar; playback asli dan volume Windows sumber tidak diubah.");
    }

    private sealed class Session
    {
        public string SourceId { get; }
        public string SourceName { get; }
        public bool SourceIsListener { get; }
        public IReadOnlyDictionary<string, string> OutputNames { get; }
        public Dictionary<string, OutputStream> Outputs { get; } = new(StringComparer.Ordinal);
        public WaveFormat CaptureFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public AudioBufferManager Buffers { get; }
        public MMDevice? SourceDevice;
        public WasapiRecorder? Recorder;
        public CaptureDataAvailableHandler? DataHandler;
        public EventHandler<StoppedEventArgs>? RecordingStoppedHandler;
        public Exception? StartupError;
        public int AcceptAudio;
        public bool CaptureStartReturned;

        public Session(string sourceId, string sourceName, bool sourceIsListener,
            IReadOnlyDictionary<string, string> outputNames)
        {
            SourceId = sourceId;
            SourceName = sourceName;
            SourceIsListener = sourceIsListener;
            OutputNames = outputNames;
            Buffers = new AudioBufferManager(CaptureFormat);
        }
    }

    private sealed class OutputStream : IWaveProvider
    {
        private int _readCount;
        private int _started;
        public string Id { get; }
        public string Name { get; }
        public FanOutWaveProvider Provider { get; }
        public MMDevice? Device;
        public WasapiPlayer? Player;
        public EventHandler<StoppedEventArgs>? StoppedHandler;
        public WaveFormat WaveFormat => Provider.WaveFormat;
        public bool Started => Volatile.Read(ref _started) != 0;

        public OutputStream(string id, string name, FanOutWaveProvider provider)
        {
            Id = id;
            Name = name;
            Provider = provider;
        }

        public int Read(Span<byte> buffer)
        {
            var count = Provider.Read(buffer);
            // Play() only starts a thread. Its first Read prefills before AudioClient.Start;
            // a later Read proves that WASAPI actually started and requested more frames.
            if (!Started && ++_readCount >= 2)
                Volatile.Write(ref _started, 1);
            return count;
        }
    }
}
