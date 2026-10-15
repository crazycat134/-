using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using ScreenReceiver.Desktop.Capture;
using ScreenReceiver.Server;
using ScreenReceiver.Server.Services;

namespace ScreenReceiver.Desktop;

public partial class MainWindow : Window
{
    private WebApplication? _app;
    private SessionService? _session;
    private IScreenCaptureService? _capture;
    private System.Threading.Timer? _uploadTimer;
    private long _frameId;
    private int _serverPort;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _session = new SessionService();
            _serverPort = GetFreePort();
            var url = $"http://0.0.0.0:{_serverPort}";

            _app = ServerHost.Build(Array.Empty<string>(), url, _session);
            await _app.StartAsync();

            var lanIp = GetLanIpAddress();
            var pairingUrl = $"http://{lanIp}:{_serverPort}/?token={_session.Token}";
            UrlBox.Text = pairingUrl;
            StatusText.Text = $"Server running on {lanIp}:{_serverPort}";
            StartButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to start server: {ex.Message}";
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        _session.StartSharing();
        _capture = new ScreenCaptureService();
        _frameId = 0;
        _uploadTimer = new System.Threading.Timer(async _ => await CaptureAndUploadAsync(),
            null, dueTime: 0, period: 1000);
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusText.Text = "Sharing…";
    }

    private async Task CaptureAndUploadAsync()
    {
        if (_capture is null || _session is null) return;

        Bitmap? bmp = null;
        try
        {
            bmp = _capture.CaptureScreen();
            if (bmp is null) return;

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Jpeg);
            var bytes = ms.ToArray();
            var frameId = Interlocked.Increment(ref _frameId);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _session.Token);
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            content.Headers.Add("X-Frame-Id", frameId.ToString());

            var response = await http.PostAsync(
                $"http://127.0.0.1:{_serverPort}/api/v1/frames", content);

            Dispatcher.Invoke(() =>
            {
                FrameInfoText.Text =
                    $"Frame #{frameId}  {bytes.Length / 1024} KB  " +
                    $"{DateTime.Now:HH:mm:ss}  HTTP {(int)response.StatusCode}";
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => StatusText.Text = $"Upload error: {ex.Message}");
        }
        finally
        {
            bmp?.Dispose();
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _uploadTimer?.Dispose();
        _uploadTimer = null;
        _capture?.Dispose();
        _capture = null;
        _session?.StopSharing();
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        StatusText.Text = "Sharing stopped.";
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            _uploadTimer?.Dispose();
            _capture?.Dispose();
            if (_app is not null)
            {
                _app.StopAsync().GetAwaiter().GetResult();
            }
        }
        catch
        {
            // Best-effort shutdown; do not block close on shutdown errors.
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string GetLanIpAddress()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            string? fallback = null;
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily != AddressFamily.InterNetwork ||
                    IPAddress.IsLoopback(ip)) continue;

                if (IsCgnat(ip)) continue; // skip Tailscale / 100.64.0.0/10

                if (IsPrivateRfc1918(ip)) return ip.ToString();
                fallback ??= ip.ToString();
            }
            return fallback ?? "127.0.0.1";
        }
        catch { return "127.0.0.1"; }
    }

    private static bool IsPrivateRfc1918(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 ||
               (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
               (b[0] == 192 && b[1] == 168);
    }

    private static bool IsCgnat(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        // 100.64.0.0/10 — shared address space (Tailscale, carrier NAT)
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }
}
