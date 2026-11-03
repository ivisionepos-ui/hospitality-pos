using Pos.Domain.Common;
using Pos.Hub;
using Pos.Hub.Api;
using Pos.Hub.Backups;
using Pos.Hub.Data;
using Pos.Hub.Live;
using Pos.Hub.Printing;
using Pos.Hub.Security;

// The Store Hub: runs on one PC in the shop, keeps the shop trading without internet,
// and serves the tills, kitchen screens and customer displays on the shop network.
var builder = WebApplication.CreateBuilder(args);

// Plain HTTP only for this PC; tills and screens on the shop network use HTTPS with the shop's own certificate.
builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    var hub = context.Configuration.GetSection("Hub").Get<HubSettings>() ?? new HubSettings();
    kestrel.ListenLocalhost(hub.LocalHttpPort);
    if (hub.NetworkHttpsPort > 0)
    {
        kestrel.ListenAnyIP(hub.NetworkHttpsPort, listen => listen.UseHttps(https =>
            https.ServerCertificateSelector = (_, _) => kestrel.ApplicationServices.GetRequiredService<HubCertificates>().Server));
    }
});

builder.Services.AddSingleton(sp =>
{
    var settings = sp.GetRequiredService<IConfiguration>().GetSection("Hub").Get<HubSettings>() ?? new HubSettings();
    settings.Validate();
    return settings;
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => InstanceLock.Acquire(InstanceLock.FolderFor(sp.GetRequiredService<HubSettings>().DatabasePath)));
builder.Services.AddSingleton(sp =>
{
    _ = sp.GetRequiredService<InstanceLock>(); // never open the database while another hub or a restore is using it
    return new HubDatabase(sp.GetRequiredService<HubSettings>().DatabasePath);
});
builder.Services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection("Backup").Get<BackupSettings>() ?? new BackupSettings());
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService<BackupWorker>();
builder.Services.AddSingleton<MenuService>();
builder.Services.AddSingleton<OrderService>();
builder.Services.AddSingleton<TillService>();
builder.Services.AddSingleton<Pos.Hub.Security.AccessService>();
builder.Services.AddSingleton(sp => new HubCertificates(sp.GetRequiredService<HubSettings>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddHostedService<CertificateRenewal>();
builder.Services.ConfigureHttpJsonOptions(o => PosJson.Configure(o.SerializerOptions));
builder.Services.AddSingleton<LiveUpdates>();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection("Printing").Get<PrintSettings>() ?? new PrintSettings());
builder.Services.AddSingleton(sp =>
{
    var printing = sp.GetRequiredService<PrintSettings>();
    return new Renderer(printing.FontFile, printing.BoldFontFile);
});
builder.Services.AddSingleton<PrintOutputs>();
builder.Services.AddSingleton<PrintService>();
builder.Services.AddHostedService<PrintWorker>();
builder.Services.AddSignalR().AddJsonProtocol(o => PosJson.Configure(o.PayloadSerializerOptions));

var app = builder.Build();

// "Pos.Hub --print-test": print a sample receipt and kitchen ticket on the configured printers, then stop.
if (args.Contains("--print-test", StringComparer.OrdinalIgnoreCase))
{
    return PrintTest.Run(app.Services.GetRequiredService<PrintOutputs>(), app.Services.GetRequiredService<PrintSettings>(), Console.Out);
}

// "Pos.Hub --restore <backup.zip>": put a backup back, with the hub closed, then stop.
var restoreAt = Array.FindIndex(args, a => string.Equals(a, "--restore", StringComparison.OrdinalIgnoreCase));
if (restoreAt >= 0)
{
    return BackupService.Restore(restoreAt + 1 < args.Length ? args[restoreAt + 1] : string.Empty, app.Services.GetRequiredService<HubSettings>(), Console.Out);
}

// Check the settings and open the database (upgrading it if needed) before taking any requests.
try
{
    _ = app.Services.GetRequiredService<InstanceLock>();
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

_ = app.Services.GetRequiredService<OrderService>();
_ = app.Services.GetRequiredService<Pos.Hub.Security.AccessService>();
var certificates = app.Services.GetRequiredService<HubCertificates>();
HubLog.CertificatesLoaded(app.Logger, certificates);

// The till screen (wwwroot/pos): plain files served by the hub, so tills need nothing installed and work without internet.
// Strict browser rules: the page may only load its own files and talk to this hub.
app.Use(async (http, next) =>
{
    if (!http.Request.Path.StartsWithSegments("/api"))
    {
        var headers = http.Response.Headers;
        headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
    }

    await next(http);
});
app.MapGet("/", () => Results.Redirect("/pos/"));
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Always check for a newer version, so a hub update reaches every till on the next reload.
    OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = "no-cache",
});

app.MapHubApi();

// Live connection: the ticket from POST /api/live/ticket must be valid and the device still registered.
app.UseWhen(http => http.Request.Path.StartsWithSegments("/live"), live => live.Use(async (http, next) =>
{
    // Browsers send the ticket as ?access_token= on the WebSocket and as "Authorization: Bearer" when negotiating.
    var ticket = http.Request.Query["access_token"].ToString();
    var authorization = http.Request.Headers.Authorization.ToString();
    if (string.IsNullOrEmpty(ticket) && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        ticket = authorization["Bearer ".Length..].Trim();
    }

    var device = http.RequestServices.GetRequiredService<LiveUpdates>().RedeemTicket(ticket);
    if (device is null || !http.RequestServices.GetRequiredService<AccessService>().IsDeviceActive(device.Id))
    {
        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    http.Items[LiveHub.DeviceItem] = device;
    await next(http);
}));
app.MapHub<LiveHub>("/live", o => o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets);
app.Run();
return 0;

/// <summary>Entry point, visible to the automated tests.</summary>
public partial class Program;
