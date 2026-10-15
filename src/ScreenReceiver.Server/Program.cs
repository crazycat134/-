using ScreenReceiver.Server;
using ScreenReceiver.Server.Services;

// Standalone entry point: `dotnet run` from this project runs the server on :5000.
// The WPF Desktop project calls ServerHost.Build directly, so this Program is only
// used when testing the server on its own.
var session = new SessionService();
Console.WriteLine($"Session token: {session.Token}");
Console.WriteLine("Listening on http://0.0.0.0:5000");

var app = ServerHost.Build(args, "http://0.0.0.0:5000", session);
await app.RunAsync();
