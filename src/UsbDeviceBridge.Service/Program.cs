using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using UsbDeviceBridge.Service.Domain;
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
builder.Services.AddSingleton<WslInterop>();
builder.Services.AddSingleton<ICommandRunner, CommandRunner>();
builder.Services.AddSingleton<VersionInfoProvider>();
builder.Services.AddSingleton<RememberedDeviceStore>();
builder.Services.AddSingleton<AutoAttachActivityTracker>();
builder.Services.AddSingleton<AutoAttachAttemptCancellationRegistry>();
builder.Services.AddSingleton<AutoAttachNotificationStore>();
builder.Services.AddSingleton<ServiceClientConnectionTracker>();
builder.Services.AddHostedService<AutoAttachBackgroundService>();

var app = builder.Build();

app.MapGrpcService<DeviceServiceImpl>();
app.MapGrpcService<AutoAttachServiceImpl>();
app.MapGrpcService<SetupServiceImpl>();
app.MapGet("/", () => "UsbDeviceBridge.Service — gRPC on :5205");

var remembered = app.Services.GetRequiredService<RememberedDeviceStore>();
var usbipd = app.Services.GetRequiredService<UsbIpdClient>();
app.Logger.LogInformation(
    "Service starting. usbipd={Path} remembered={File}",
    usbipd.UsbIpdPath,
    remembered.FilePath
);

app.Run();
