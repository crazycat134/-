using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using NAudio.Wave;
using ScreenReceiver.Server.Services;

namespace ScreenReceiver.Desktop.Capture;

/// <summary>
/// Captures system audio output (WASAPI loopback) and feeds 4-second WAV chunks
/// to the InterviewAssistantService. No phone mic needed — we hear the interviewer
/// directly from the sound card.
/// </summary>
public sealed class SystemAudioCaptureService : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private readonly ConcurrentQueue<byte[]> _chunkQueue = new();
    private readonly MemoryStream _buffer = new();
    private readonly object _bufLock = new();
    private System.Threading.Timer? _flushTimer;
    private bool _muted;

    private readonly InterviewAssistantService _assistant;
    private readonly CancellationTokenSource _cts = new();

    public bool IsRunning { get; private set; }
    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            Debug.WriteLine($"[Audio] Muted={value}");
        }
    }

    public SystemAudioCaptureService(InterviewAssistantService assistant)
    {
        _assistant = assistant;
    }

    public void Start()
    {
        if (IsRunning) return;
        _capture = new WasapiLoopbackCapture();
        Debug.WriteLine($"[Audio] Capture format: {_capture.WaveFormat}");

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += (s, e) => Debug.WriteLine("[Audio] Recording stopped");

        _capture.StartRecording();
        // Flush buffer every 4 seconds
        _flushTimer = new System.Threading.Timer(_ => FlushBuffer(), null, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(2.5));
        IsRunning = true;
        Console.WriteLine($"[Audio] Loopback capture started, format={_capture.WaveFormat}");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_assistant.Muted || e.BytesRecorded == 0) return;
        lock (_bufLock)
        {
            _buffer.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    private void FlushBuffer()
    {
        if (!IsRunning || _assistant.Muted) return;
        lock (_bufLock)
        {
            if (_buffer.Length < 8000)
            {
                // Console.WriteLine($"[Audio] Buffer too small: {_buffer.Length} bytes (silence)");
                return;
            }
            var wavBytes = BuildWav(_buffer.ToArray());
            _buffer.SetLength(0);
            Console.WriteLine($"[Audio] Sending {wavBytes.Length} bytes to Whisper...");
            _ = Task.Run(async () =>
            {
                try { await _assistant.ProcessWavChunkAsync(wavBytes, _cts.Token); }
                catch (Exception ex) { Console.WriteLine($"[Audio] Error: {ex}"); }
            });
        }
    }

    /// <summary>Manually convert WASAPI 32-bit float 48kHz stereo to 16kHz 16-bit mono PCM WAV.</summary>
    private byte[] BuildWav(byte[] pcm)
    {
        var srcFormat = _capture!.WaveFormat;
        int srcChannels = srcFormat.Channels;       // 2
        int srcRate = srcFormat.SampleRate;          // 48000
        int targetRate = 16000;
        int sampleCount = pcm.Length / 4;           // 4 bytes per float
        int step = srcRate / targetRate;            // 3 (48000/16000)

        using var outMs = new MemoryStream();
        using var bw = new BinaryWriter(outMs);

        // Write WAV header (filled in later)
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(0); // size placeholder
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)1); // mono
        bw.Write(targetRate);
        bw.Write(targetRate * 2); // bytes/sec (16-bit mono)
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(0); // size placeholder

        long dataStart = outMs.Position;

        // Convert float samples to 16-bit, downmix stereo to mono, downsample
        for (int i = 0; i < sampleCount; i += step * srcChannels)
        {
            // Average channels
            float sum = 0;
            for (int ch = 0; ch < srcChannels && i + ch < sampleCount; ch++)
            {
                sum += BitConverter.ToSingle(pcm, (i + ch) * 4);
            }
            float mono = sum / srcChannels;
            short s = (short)(mono * 32767);
            bw.Write(s);
        }

        long dataSize = outMs.Position - dataStart;
        outMs.Position = 4;
        bw.Write((int)(outMs.Length - 8));
        outMs.Position = dataStart - 4;
        bw.Write((int)dataSize);
        outMs.Position = outMs.Length;

        return outMs.ToArray();
    }

    public void Stop()
    {
        IsRunning = false;
        _flushTimer?.Dispose();
        _capture?.StopRecording();
    }

    public void Dispose()
    {
        Stop();
        _capture?.Dispose();
        _cts.Dispose();
    }
}
