using Pos.Domain.Common;
using Pos.Hub;
using Pos.Hub.Api;
using Pos.Hub.Data;

// The Store Hub: runs on one PC in the shop, keeps the shop trading without internet,
// and serves the tills, kitchen screens and customer displays on the shop network.
var builder = WebApplication.CreateBuilder(args);

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
builder.Services.ConfigureHttpJsonOptions(o => PosJson.Configure(o.SerializerOptions));

var app = builder.Build();

// Check the settings and open the database (upgrading it if needed) before taking any requests.
_ = app.Services.GetRequiredService<OrderService>();
_ = app.Services.GetRequiredService<Pos.Hub.Security.AccessService>();

app.MapHubApi();
app.Run();

/// <summary>Entry point, visible to the automated tests.</summary>
public partial class Program;
