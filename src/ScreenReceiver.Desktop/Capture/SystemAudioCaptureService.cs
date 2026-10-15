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
        _flushTimer = new Timer(_ => FlushBuffer(), null, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4));
        IsRunning = true;
        Debug.WriteLine("[Audio] Started loopback capture");
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
        byte[]? wavBytes = null;
        lock (_bufLock)
        {
            if (_buffer.Length < 8000) return; // too short, probably silence
            wavBytes = BuildWav(_buffer.ToArray());
            _buffer.SetLength(0);
        }
        if (wavBytes != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _assistant.ProcessWavChunkAsync(wavBytes, _cts.Token);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Audio] Process error: {ex.Message}");
                }
            });
        }
    }

    /// <summary>Wrap raw PCM bytes with a WAV header so Whisper accepts it.</summary>
    private byte[] BuildWav(byte[] pcm)
    {
        // WASAPI loopback gives us the mix format (usually 32-bit float, 48kHz, stereo).
        // Whisper handles wav fine; we just need a valid header.
        var fmt = _capture!.WaveFormat;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        // RIFF header
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + pcm.Length);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        // fmt chunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)fmt.Encoding);
        bw.Write((short)fmt.Channels);
        bw.Write(fmt.SampleRate);
        bw.Write(fmt.AverageBytesPerSecond);
        bw.Write((short)fmt.BlockAlign);
        bw.Write((short)fmt.BitsPerSample);
        // data chunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(pcm.Length);
        bw.Write(pcm);
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
