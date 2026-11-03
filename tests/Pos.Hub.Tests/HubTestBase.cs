namespace Pos.Hub.Tests;

/// <summary>Starts a hub on a fresh database, sets up a shop, and gives clients for each person.</summary>
public abstract class HubTestBase : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pos-hub-tests", Guid.NewGuid().ToString("N"));
    private readonly List<HttpClient> _clients = [];

    internal TestClock Clock { get; } = new(new DateTimeOffset(2026, 11, 5, 18, 0, 0, TimeSpan.Zero));

    internal HubTestHost Host { get; private set; } = null!;

    internal TestShop Shop { get; private set; } = null!;

    internal string DbPath => Path.Combine(_folder, "hub.db");

    /// <summary>Client with no device credentials.</summary>
    internal HttpClient Anonymous { get; private set; } = null!;

    internal HttpClient Owner { get; private set; } = null!;

    internal HttpClient Cashier { get; private set; } = null!;

    internal HttpClient Manager { get; private set; } = null!;

    internal HttpClient Kitchen { get; private set; } = null!;

    /// <summary>The till with nobody logged in (like a customer display).</summary>
    internal HttpClient TillOnly { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        Host = new HubTestHost(DbPath, Clock);
        Shop = await TestShop.SetUp(Host);
        MakeClients();
    }

    public virtual Task DisposeAsync()
    {
        DisposeClients();
        Host.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Temp files; leftovers are harmless.
        }

        return Task.CompletedTask;
    }

    /// <summary>Stops the hub and starts it again on the same database, like a PC restart. Logins stay valid.</summary>
    internal void Restart()
    {
        DisposeClients();
        Host.Dispose();
        Host = new HubTestHost(DbPath, Clock);
        MakeClients();
    }

    internal HttpClient ClientFor(Credentials who)
    {
        var client = Host.Client(who);
        _clients.Add(client);
        return client;
    }

    private void MakeClients()
    {
        Anonymous = Host.CreateClient();
        _clients.Add(Anonymous);
        Owner = ClientFor(Shop.Owner);
        Cashier = ClientFor(Shop.Cashier);
        Manager = ClientFor(Shop.Manager);
        Kitchen = ClientFor(Shop.Kitchen);
        TillOnly = ClientFor(Shop.Till);
    }

    private void DisposeClients()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        _clients.Clear();
    }
}
