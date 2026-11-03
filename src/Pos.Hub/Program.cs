using Pos.Domain.Common;
using Pos.Hub;
using Pos.Hub.Api;
using Pos.Hub.Data;
using Pos.Hub.Live;
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
builder.Services.AddSingleton(sp => new HubDatabase(sp.GetRequiredService<HubSettings>().DatabasePath));
builder.Services.AddSingleton<MenuService>();
builder.Services.AddSingleton<OrderService>();
builder.Services.AddSingleton<Pos.Hub.Security.AccessService>();
builder.Services.AddSingleton(sp => new HubCertificates(sp.GetRequiredService<HubSettings>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddHostedService<CertificateRenewal>();
builder.Services.ConfigureHttpJsonOptions(o => PosJson.Configure(o.SerializerOptions));
builder.Services.AddSingleton<LiveUpdates>();
builder.Services.AddSignalR().AddJsonProtocol(o => PosJson.Configure(o.PayloadSerializerOptions));

var app = builder.Build();

// Check the settings and open the database (upgrading it if needed) before taking any requests.
_ = app.Services.GetRequiredService<OrderService>();
_ = app.Services.GetRequiredService<Pos.Hub.Security.AccessService>();
var certificates = app.Services.GetRequiredService<HubCertificates>();
HubLog.CertificatesLoaded(app.Logger, certificates);

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

/// <summary>Entry point, visible to the automated tests.</summary>
public partial class Program;
