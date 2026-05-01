using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using UsbDeviceBridge.Service.Interop;
using UsbDeviceBridge.Service.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();

// Listen on loopback HTTP/2 only — gRPC requires HTTP/2.
// TLS is skipped intentionally; the service is localhost-only.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, 5205, endpoint =>
    {
        endpoint.Protocols = HttpProtocols.Http2;
    });
});

builder.Services.AddGrpc();
builder.Services.AddSingleton<UsbIpdClient>();
builder.Services.AddSingleton<ICommandRunner, CommandRunner>();
builder.Services.AddSingleton<VersionInfoProvider>();

// REMOVED (BUG-0006 fix — moved to app process):
// - RememberedDeviceStore
// - AutoAttachActivityTracker
// - AutoAttachAttemptCancellationRegistry
// - AutoAttachNotificationStore
// - AutoAttachBackgroundService

var app = builder.Build();

app.MapGrpcService<AdminServiceImpl>();
app.MapGrpcService<DeviceServiceImpl>();
app.MapGrpcService<SetupServiceImpl>();
app.MapGet("/", () => "UsbDeviceBridge.Service — gRPC on :5205");

var usbipd = app.Services.GetRequiredService<UsbIpdClient>();
app.Logger.LogInformation("Service starting. usbipd={Path}", usbipd.UsbIpdPath);

app.Run();
