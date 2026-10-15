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
                catch (Exception ex) { Console.WriteLine($"[Audio] Error: {ex.Message}"); }
            });
        }
    }

    /// <summary>Wrap raw float bytes as a 16kHz 16-bit mono WAV (Whisper-friendly).</summary>
    private byte[] BuildWav(byte[] pcm)
    {
        // WASAPI gives us 32-bit float 48kHz stereo. Whisper wants 16kHz 16-bit mono PCM.
        var sourceFormat = _capture!.WaveFormat;
        using var sourceStream = new RawSourceWaveStream(new MemoryStream(pcm), sourceFormat);
        var targetFormat = new WaveFormat(16000, 16, 1);
        using var conversion = new WaveFormatConversionStream(targetFormat, sourceStream);
        using var outMs = new MemoryStream();
        // Copy converted PCM to a temp buffer
        var buffer = new byte[targetFormat.AverageBytesPerSecond];
        int read;
        while ((read = conversion.Read(buffer, 0, buffer.Length)) > 0)
            outMs.Write(buffer, 0, read);
        var converted = outMs.ToArray();

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + converted.Length);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)1); // mono
        bw.Write(16000);
        bw.Write(16000 * 2); // bytes/sec
        bw.Write((short)2);  // block align
        bw.Write((short)16);  // bits/sample
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(converted.Length);
        bw.Write(converted);
        return ms.ToArray();
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
