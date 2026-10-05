using System.Runtime.InteropServices;
using MultiHeadsetAudioSharing.Core;
using NAudio.Wave;

if (args.Contains("--devices"))
{
    using var devices = new AudioDeviceManager();
    foreach (var device in devices.GetDevices())
        Console.WriteLine($"{device.State} | {device.Name} | {device.Id}");
    Console.WriteLine($"Default source: {devices.GetDefaultSourceId() ?? "none"}");
    return;
}
if (args.Contains("--lifecycle-smoke"))
{
    await RunLifecycleSmoke();
    return;
}
if (args.Contains("--smoke"))
{
    await RunAudioSmoke(args);
    return;
}

var tests = new (string Name, Action Run)[]
{
    ("Independent readers and gain isolation", IndependentReaders),
    ("Overflow drops oldest complete stereo frames only for slow reader", SlowReader),
    ("Oversized packet retains newest frames", OversizedPacket),
    ("Wrapped ring preserves order", RingWrap),
    ("Silence is live and resume does not replay consumed audio", SilenceResume),
    ("Removed reader cannot receive future capture packets", RemoveReader),
    ("Partial PCM frames and invalid gains rejected", InvalidValues)
};
foreach (var test in tests)
{
    test.Run();
    Console.WriteLine($"PASS {test.Name}");
}
Console.WriteLine($"{tests.Length} behavioral tests passed.");

static AudioBufferManager CreateBuffer(int milliseconds = 4) =>
    new(WaveFormat.CreateIeeeFloatWaveFormat(1000, 2), milliseconds);

static byte[] Pcm(params float[] samples) => MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
static float[] Read(FanOutWaveProvider reader, int frames)
{
    var bytes = new byte[frames * reader.WaveFormat.BlockAlign];
    if (reader.Read(bytes) != bytes.Length) throw new Exception("Live stream returned EOF.");
    return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
}
static void Equal(float[] actual, params float[] expected)
{
    if (!actual.SequenceEqual(expected))
        throw new Exception($"Expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}].");
}
static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static void IndependentReaders()
{
    var manager = CreateBuffer();
    var a = manager.AddOutput("A", 1);
    var b = manager.AddOutput("B", 0.5f);
    manager.Write(Pcm(0.8f, -0.8f, 0.4f, -0.4f));
    Equal(Read(a, 1), 0.8f, -0.8f);
    Equal(Read(b, 2), 0.4f, -0.4f, 0.2f, -0.2f);
    a.Gain = 0;
    Equal(Read(a, 1), 0, 0);
    manager.Write(Pcm(0.6f, -0.6f));
    Equal(Read(a, 1), 0, 0);
    Equal(Read(b, 1), 0.3f, -0.3f);
}
static void SlowReader()
{
    var manager = CreateBuffer(3);
    var fast = manager.AddOutput("fast", 1);
    var slow = manager.AddOutput("slow", 1);
    manager.Write(Pcm(1, -1, 2, -2));
    Equal(Read(fast, 2), 1, -1, 2, -2);
    manager.Write(Pcm(3, -3, 4, -4));
    Equal(Read(fast, 2), 3, -3, 4, -4);
    Equal(Read(slow, 3), 2, -2, 3, -3, 4, -4);
    Require(slow.DroppedFrames == 1 && fast.DroppedFrames == 0, "Slow-reader drops leaked to another reader.");
}
static void OversizedPacket()
{
    var manager = CreateBuffer(2);
    var reader = manager.AddOutput("A", 1);
    manager.Write(Pcm(1, -1));
    manager.Write(Pcm(2, -2, 3, -3, 4, -4, 5, -5));
    Equal(Read(reader, 2), 4, -4, 5, -5);
    Require(reader.DroppedFrames == 3, "Overflow count must include discarded old and incoming frames.");
}
static void RingWrap()
{
    var manager = CreateBuffer(3);
    var reader = manager.AddOutput("A", 1);
    manager.Write(Pcm(1, -1, 2, -2));
    Equal(Read(reader, 1), 1, -1);
    manager.Write(Pcm(3, -3, 4, -4));
    Equal(Read(reader, 3), 2, -2, 3, -3, 4, -4);
}
static void SilenceResume()
{
    var manager = CreateBuffer();
    var reader = manager.AddOutput("A", 1);
    manager.Write(Pcm(0.5f, -0.5f));
    Equal(Read(reader, 3), 0.5f, -0.5f, 0, 0, 0, 0);
    Equal(Read(reader, 1), 0, 0);
    Require(reader.UnderrunFrames == 3, "Underrun frames not counted.");
    manager.Write(Pcm(0.25f, -0.25f));
    Equal(Read(reader, 1), 0.25f, -0.25f);
}
static void RemoveReader()
{
    var manager = CreateBuffer();
    var reader = manager.AddOutput("A", 1);
    manager.Write(Pcm(1, -1));
    manager.RemoveOutput("A");
    manager.Write(Pcm(0.5f, -0.5f));
    Equal(Read(reader, 1), 0, 0);
    var replacement = manager.AddOutput("A", 1);
    manager.Write(Pcm(0.25f, -0.25f));
    Equal(Read(replacement, 1), 0.25f, -0.25f);
    Equal(Read(reader, 1), 0, 0);
}
static void InvalidValues()
{
    var manager = CreateBuffer();
    var reader = manager.AddOutput("A", 1);
    Throws<ArgumentException>(() => manager.Write(new byte[4]));
    Throws<ArgumentException>(() => reader.Read(new byte[4]));
    Throws<ArgumentOutOfRangeException>(() => reader.Gain = float.NaN);
    Throws<ArgumentOutOfRangeException>(() => reader.Gain = -0.1f);
    Throws<ArgumentOutOfRangeException>(() => reader.Gain = 1.1f);
    Throws<ArgumentException>(() => new AudioBufferManager(new WaveFormat(48000, 16, 2)));
}

static async Task RunLifecycleSmoke()
{
    using var devices = new AudioDeviceManager();
    var active = devices.GetDevices().Where(d => d.IsActive).ToArray();
    string sourceId = devices.GetDefaultSourceId() ?? throw new InvalidOperationException("No active source.");
    var outputs = active.Where(d => d.Id != sourceId).Take(2)
        .Select(d => new OutputSelection(d.Id, 1)).ToArray();
    if (outputs.Length == 0) throw new InvalidOperationException("Lifecycle smoke requires two active endpoints.");
    await using var session = new SessionCoordinator(devices);
    try
    {
        await session.StartAsync(sourceId, true, []);
        throw new Exception("A one-listener Start was accepted.");
    }
    catch (InvalidOperationException ex) when (ex.InnerException is ArgumentException)
    {
        Require(!session.GetStatus().IsRunning, "Rejected Start left a running session.");
        Console.WriteLine("PASS one-listener Start rejected without a live session.");
    }
    for (int cycle = 1; cycle <= 3; cycle++)
    {
        await session.StartAsync(sourceId, outputs.Length == 1, outputs);
        await Task.Delay(500);
        var status = session.GetStatus();
        Require(status.IsRunning && status.Outputs.Count == outputs.Length, "Real output startup failed.");
        session.SetGain(outputs[0].DeviceId, 0);
        var changed = session.GetStatus();
        Require(changed.Outputs.Single(o => o.DeviceId == outputs[0].DeviceId).Gain == 0, "Gain change failed.");
        if (outputs.Length > 1)
            Require(changed.Outputs.Single(o => o.DeviceId == outputs[1].DeviceId).Gain == 1, "Gain leaked to output B.");
        await session.StopAsync();
        Require(!session.GetStatus().IsRunning && session.GetStatus().Outputs.Count == 0, "Stop left live outputs.");
        Console.WriteLine($"PASS lifecycle cycle {cycle}: {outputs.Length} real redistribution streams, live gain, Stop; input peak={status.InputPeak:F6}.");
    }
    Console.WriteLine("LIMIT: this exercises stream lifecycle only; non-silent audio requires the separate --smoke check.");
}

static async Task RunAudioSmoke(string[] arguments)
{
    using var devices = new AudioDeviceManager();
    var active = devices.GetDevices().Where(d => d.IsActive).ToArray();
    Console.WriteLine($"Active render endpoints: {active.Length}");
    foreach (var device in active) Console.WriteLine($"  {device.Name} | {device.Id}");
    if (active.Length == 0)
        throw new InvalidOperationException("Real audio smoke requires an active Windows render endpoint.");
    int sourceOption = Array.IndexOf(arguments, "--source");
    string sourceId = sourceOption >= 0 && sourceOption + 1 < arguments.Length
        ? active.Single(d => d.Id == arguments[sourceOption + 1] || d.Name == arguments[sourceOption + 1]).Id
        : devices.GetDefaultSourceId() ?? active[0].Id;
    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    using var sourceDevice = enumerator.GetDevice(sourceId);
    var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    await using var recorder = new WasapiRecorderBuilder().WithDevice(sourceDevice)
        .WithLoopbackCapture().WithFormat(format).WithBufferLength(20).Build();
    long capturedBytes = 0;
    float capturedPeak = 0;
    recorder.DataAvailable += (buffer, flags, position, qpc) =>
    {
        Interlocked.Add(ref capturedBytes, buffer.Length);
        float peak = 0;
        foreach (float sample in MemoryMarshal.Cast<byte, float>(buffer))
            peak = Math.Max(peak, Math.Abs(sample));
        Volatile.Write(ref capturedPeak, Math.Max(Volatile.Read(ref capturedPeak), peak));
    };
    await using var originalPlayback = new WasapiPlayerBuilder().WithDevice(sourceDevice)
        .WithSharedMode().WithLatency(40).Build();
    originalPlayback.Init(new ToneProvider(format));
    recorder.RecordingStopped += (_, e) =>
    {
        if (e.Exception is not null) Console.Error.WriteLine($"Recorder failed: {e.Exception}");
    };
    originalPlayback.PlaybackStopped += (_, e) =>
    {
        if (e.Exception is not null) Console.Error.WriteLine($"Original playback failed: {e.Exception}");
    };
    recorder.StartRecording();
    originalPlayback.Play();
    await Task.Delay(1500);
    Console.WriteLine($"Capture diagnostic: bytes={capturedBytes}, peak={capturedPeak:F6}, recorder={recorder.CaptureState} ({recorder.WaveFormat}), player={originalPlayback.PlaybackState}, playedBytes={originalPlayback.GetPosition()}, sourceMuted={sourceDevice.AudioEndpointVolume.Mute}, sourceVolume={sourceDevice.AudioEndpointVolume.MasterVolumeLevelScalar:F3}, sessionVolume={originalPlayback.Volume:F3}, sessionMuted={originalPlayback.IsMuted}, sourcePeak={sourceDevice.AudioMeterInformation.MasterPeakValue:F6}");
    Require(Interlocked.Read(ref capturedBytes) > 0 && Volatile.Read(ref capturedPeak) > 0.001f,
        "WASAPI loopback did not capture the real source tone.");
    Console.WriteLine($"PASS real WASAPI playback/loopback: {capturedBytes} bytes, peak {capturedPeak:F4}");
    recorder.StopRecording();

    var redistributed = active.Where(d => d.Id != sourceId).Take(2).ToArray();
    await using var session = new SessionCoordinator(devices);
    if (redistributed.Length == 0)
    {
        try
        {
            await session.StartAsync(sourceId, true, []);
            throw new Exception("One-listener session unexpectedly accepted.");
        }
        catch (InvalidOperationException ex) when (ex.InnerException is ArgumentException)
        {
            Require(!session.GetStatus().IsRunning, "Rejected session leaked running state.");
        }
        Console.WriteLine("PASS insufficient-listener validation on real engine.");
        Console.WriteLine("LIMIT: no second active endpoint; real fan-out/disconnect/gain acoustic verification unavailable.");
    }
    else
    {
        bool sourceListener = redistributed.Length == 1;
        var selections = redistributed.Select(d => new OutputSelection(d.Id, 0.3f)).ToArray();
        for (int cycle = 1; cycle <= 3; cycle++)
        {
            await session.StartAsync(sourceId, sourceListener, selections);
            await Task.Delay(1000);
            var status = session.GetStatus();
            Require(status.IsRunning && status.Outputs.Count == selections.Length && status.InputPeak > 0.001f,
                $"Session startup/capture failed: {status.Message}");
            session.SetGain(selections[0].DeviceId, 0);
            Require(session.GetStatus().Outputs.Single(o => o.DeviceId == selections[0].DeviceId).Gain == 0,
                "Live output gain did not change.");
            originalPlayback.Stop();
            await Task.Delay(800);
            Require(session.GetStatus().IsRunning, "Pausing the test player unexpectedly stopped session.");
            originalPlayback.Play();
            await Task.Delay(800);
            Require(session.GetStatus().IsRunning && session.GetStatus().InputPeak > 0.001f,
                "Capture did not deliver non-silent input after the test player resumed.");
            await session.StopAsync();
            Require(!session.GetStatus().IsRunning && session.GetStatus().Outputs.Count == 0,
                "Stop left live output state.");
            Console.WriteLine($"PASS real session cycle {cycle}: {selections.Length} redistribution output(s), gain, test-player pause/resume, Stop.");
        }
        Console.WriteLine("LIMIT: audible gain independence and physical disconnect require manual hardware observation.");
    }
    originalPlayback.Stop();
}

sealed class ToneProvider : IWaveProvider
{
    private readonly byte[] period;
    private int offset;
    public ToneProvider(WaveFormat format)
    {
        WaveFormat = format;
        var samples = new float[format.SampleRate * format.Channels];
        for (int frame = 0; frame < format.SampleRate; frame++)
        {
            float value = (float)(Math.Sin(2 * Math.PI * 440 * frame / format.SampleRate) * 0.05);
            for (int channel = 0; channel < format.Channels; channel++) samples[frame * format.Channels + channel] = value;
        }
        period = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    }
    public WaveFormat WaveFormat { get; }
    public int Read(Span<byte> buffer)
    {
        int written = 0;
        while (written < buffer.Length)
        {
            int count = Math.Min(buffer.Length - written, period.Length - offset);
            period.AsSpan(offset, count).CopyTo(buffer[written..]);
            written += count;
            offset = (offset + count) % period.Length;
        }
        return written;
    }
}
