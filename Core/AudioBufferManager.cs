using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace MultiHeadsetAudioSharing.Core;

/// <summary>One bounded ring per listener; a slow listener never consumes another listener's audio.</summary>
public sealed class AudioBufferManager
{
    private readonly object gate = new();
    private readonly WaveFormat format;
    private readonly int capacityBytes;
    private FanOutWaveProvider[] readers = [];
    private float inputPeak;
    private long peakTimestamp;

    public AudioBufferManager(WaveFormat format, int capacityMilliseconds = 200)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.Encoding != WaveFormatEncoding.IeeeFloat || format.BitsPerSample != 32)
            throw new ArgumentException("Fan-out requires 32-bit IEEE float PCM.", nameof(format));
        if (capacityMilliseconds <= 0 || capacityMilliseconds > 5000)
            throw new ArgumentOutOfRangeException(nameof(capacityMilliseconds));
        this.format = format;
        capacityBytes = checked(Math.Max(1, format.SampleRate * capacityMilliseconds / 1000) * format.BlockAlign);
    }

    public float InputPeak => Stopwatch.GetElapsedTime(Interlocked.Read(ref peakTimestamp)) < TimeSpan.FromMilliseconds(250)
        ? Volatile.Read(ref inputPeak) : 0;

    public FanOutWaveProvider AddOutput(string id, float gain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (gate)
        {
            if (readers.Any(reader => reader.Id == id))
                throw new InvalidOperationException("Output already has a reader.");
            var reader = new FanOutWaveProvider(id, format, capacityBytes, gain);
            Volatile.Write(ref readers, [.. readers, reader]);
            return reader;
        }
    }

    public void RemoveOutput(string id)
    {
        lock (gate)
        {
            var removed = readers.FirstOrDefault(reader => reader.Id == id);
            Volatile.Write(ref readers, readers.Where(reader => reader.Id != id).ToArray());
            removed?.Deactivate();
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length % format.BlockAlign != 0)
            throw new ArgumentException("Capture packet must contain complete PCM frames.", nameof(data));
        float peak = 0;
        foreach (float sample in MemoryMarshal.Cast<byte, float>(data))
            peak = Math.Max(peak, Math.Abs(sample));
        // Hold a peak across packets so the 200 ms UI poll does not only see an intervening silent packet.
        if (peak >= Volatile.Read(ref inputPeak) ||
            Stopwatch.GetElapsedTime(Interlocked.Read(ref peakTimestamp)) >= TimeSpan.FromMilliseconds(250))
        {
            Volatile.Write(ref inputPeak, Math.Min(1, peak));
            Interlocked.Exchange(ref peakTimestamp, Stopwatch.GetTimestamp());
        }
        foreach (var reader in Volatile.Read(ref readers))
            reader.Write(data);
    }

    public void Clear()
    {
        foreach (var reader in Volatile.Read(ref readers))
            reader.Clear();
        Volatile.Write(ref inputPeak, 0);
        Interlocked.Exchange(ref peakTimestamp, 0);
    }
}

public sealed class FanOutWaveProvider : IWaveProvider
{
    private readonly object gate = new();
    private readonly byte[] ring;
    private int head;
    private int count;
    private bool active = true;
    private float gain;
    private long underrunFrames;
    private long droppedFrames;

    internal FanOutWaveProvider(string id, WaveFormat format, int capacityBytes, float gain)
    {
        Id = id;
        WaveFormat = format;
        ring = new byte[capacityBytes];
        Gain = gain;
    }

    internal string Id { get; }
    public WaveFormat WaveFormat { get; }
    public float Gain
    {
        get => Volatile.Read(ref gain);
        set
        {
            if (!float.IsFinite(value) || value < 0 || value > 1)
                throw new ArgumentOutOfRangeException(nameof(value), "Gain must be between 0 and 1.");
            Volatile.Write(ref gain, value);
        }
    }
    public int BufferedMilliseconds
    {
        get { lock (gate) return (int)((long)count * 1000 / WaveFormat.AverageBytesPerSecond); }
    }
    public long UnderrunFrames => Interlocked.Read(ref underrunFrames);
    public long DroppedFrames => Interlocked.Read(ref droppedFrames);

    internal void Write(ReadOnlySpan<byte> data)
    {
        lock (gate)
        {
            if (!active) return;
            int overflow = Math.Max(0, count + data.Length - ring.Length);
            if (overflow != 0)
            {
                Interlocked.Add(ref droppedFrames, overflow / WaveFormat.BlockAlign);
                int remove = Math.Min(count, overflow);
                head = (head + remove) % ring.Length;
                count -= remove;
                if (data.Length > ring.Length)
                    data = data[^ring.Length..];
            }
            int tail = (head + count) % ring.Length;
            int first = Math.Min(data.Length, ring.Length - tail);
            data[..first].CopyTo(ring.AsSpan(tail, first));
            data[first..].CopyTo(ring);
            count += data.Length;
        }
    }

    public int Read(Span<byte> buffer)
    {
        if (buffer.Length % WaveFormat.BlockAlign != 0)
            throw new ArgumentException("Read must request complete PCM frames.", nameof(buffer));
        int available;
        lock (gate)
        {
            available = Math.Min(count, buffer.Length);
            int first = Math.Min(available, ring.Length - head);
            ring.AsSpan(head, first).CopyTo(buffer);
            ring.AsSpan(0, available - first).CopyTo(buffer[first..]);
            head = (head + available) % ring.Length;
            count -= available;
            if (active)
                Interlocked.Add(ref underrunFrames, (buffer.Length - available) / WaveFormat.BlockAlign);
        }
        buffer[available..].Clear();
        float currentGain = Gain;
        if (currentGain != 1)
        {
            var samples = MemoryMarshal.Cast<byte, float>(buffer[..available]);
            for (int i = 0; i < samples.Length; i++) samples[i] *= currentGain;
        }
        // Silence is a live stream, not EOF. The player stays ready when capture pauses.
        return buffer.Length;
    }

    internal void Clear()
    {
        lock (gate) { head = 0; count = 0; }
    }

    internal void Deactivate()
    {
        lock (gate) { active = false; head = 0; count = 0; }
    }
}
