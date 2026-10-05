using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;

namespace MultiHeadsetAudioSharing.Core;

public record AudioDeviceInfo(string Id, string Name, bool IsActive, string State);

public sealed class AudioDeviceManager : IDisposable
{
    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDeviceNotificationClient _notifications;
    private readonly Channel<DeviceChange> _changes = Channel.CreateUnbounded<DeviceChange>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private int _disposed;

    public event EventHandler? DevicesChanged;

    // Preserve removal/inactive transitions even if the endpoint reconnects before a refresh.
    internal event Action<string>? EndpointUnavailable;

    public AudioDeviceManager()
    {
        _enumerator = new MMDeviceEnumerator();
        try
        {
            _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
            _notifications.DeviceStateChanged += OnDeviceStateChanged;
            _notifications.DeviceAdded += OnDeviceChanged;
            _notifications.DeviceRemoved += OnDeviceRemoved;
            _notifications.DefaultDeviceChanged += OnDefaultDeviceChanged;
            _notifications.PropertyValueChanged += OnPropertyValueChanged;
        }
        catch
        {
            _enumerator.Dispose();
            throw;
        }
        _ = Task.Run(DispatchChangesAsync);
    }

    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            using var endpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All);
            var result = new List<AudioDeviceInfo>(endpoints.Count);
            for (var index = 0; index < endpoints.Count; index++)
            {
                try
                {
                    using var endpoint = endpoints[index];
                    var state = endpoint.State;
                    result.Add(new AudioDeviceInfo(endpoint.ID, endpoint.FriendlyName,
                        state == DeviceState.Active, state.ToString()));
                }
                catch (COMException)
                {
                    // A collection is a snapshot: an endpoint can vanish while it is read.
                }
            }
            return result.OrderByDescending(device => device.IsActive)
                .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(device => device.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public string? GetDefaultSourceId()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!_enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var endpoint))
                return null;
            using (endpoint)
                return endpoint.ID;
        }
    }

    internal MMDevice OpenActiveDevice(string id)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var endpoint = _enumerator.GetDevice(id);
            try
            {
                if (endpoint.DataFlow != DataFlow.Render || endpoint.State != DeviceState.Active)
                    throw new InvalidOperationException($"Endpoint '{id}' is not an active playback device.");
                return endpoint;
            }
            catch
            {
                endpoint.Dispose();
                throw;
            }
        }
    }

    private void OnDeviceStateChanged(object? sender, DeviceStateChangedEventArgs args) =>
        QueueChange(args.NewState == DeviceState.Active ? null : args.DeviceId);

    private void OnDeviceChanged(object? sender, DeviceNotificationEventArgs args) => QueueChange(null);
    private void OnDeviceRemoved(object? sender, DeviceNotificationEventArgs args) => QueueChange(args.DeviceId);
    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs args) => QueueChange(null);
    private void OnPropertyValueChanged(object? sender, DevicePropertyChangedEventArgs args) => QueueChange(null);

    private void QueueChange(string? unavailableId)
    {
        // Windows holds an audio-stack lock here. Never enumerate, dispose, or invoke clients here.
        if (Volatile.Read(ref _disposed) == 0)
            _changes.Writer.TryWrite(new DeviceChange(unavailableId));
    }

    private async Task DispatchChangesAsync()
    {
        await foreach (var change in _changes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (Volatile.Read(ref _disposed) != 0)
                break;
            if (change.UnavailableId is not null && EndpointUnavailable is { } unavailable)
            {
                foreach (Action<string> handler in unavailable.GetInvocationList())
                {
                    try { handler(change.UnavailableId); }
                    catch (Exception exception) { Trace.TraceError("Endpoint notification handler failed: {0}", exception); }
                }
            }
            if (DevicesChanged is { } changed)
            {
                foreach (EventHandler handler in changed.GetInvocationList())
                {
                    try { handler(this, EventArgs.Empty); }
                    catch (Exception exception) { Trace.TraceError("Device refresh handler failed: {0}", exception); }
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed != 0)
                return;
            Volatile.Write(ref _disposed, 1);
        }
        _notifications.DeviceStateChanged -= OnDeviceStateChanged;
        _notifications.DeviceAdded -= OnDeviceChanged;
        _notifications.DeviceRemoved -= OnDeviceRemoved;
        _notifications.DefaultDeviceChanged -= OnDefaultDeviceChanged;
        _notifications.PropertyValueChanged -= OnPropertyValueChanged;
        _changes.Writer.TryComplete();
        _notifications.Dispose();
        _enumerator.Dispose();
    }

    private readonly record struct DeviceChange(string? UnavailableId);
}
