using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SundayRivalry;
using SundayRivalry.Domain;
using SundayRivalry.Services;

sealed class TestHost : IAsyncDisposable
{
    WebApplication? app;
    public string Directory { get; } = Path.GetFullPath(Path.Combine("artifacts", "test-runs", Guid.NewGuid().ToString("N")));
    public TestClock Clock { get; } = new();
    public Uri Address { get; private set; } = null!;
    public GameService Games => app!.Services.GetRequiredService<GameService>();
    public int ExternalRequests;
    public async Task Start()
    {
        // Explicit CLI paths override inherited config; no production DB or key is used.
        var root = Path.GetFullPath("src/SundayRivalry");
        app = await AppHost.Create(["--contentRoot", root, "--DataDirectory", Directory, "--urls", Address?.ToString() ?? "http://127.0.0.1:0", "--environment", "Testing"], builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Tank01:ApiKey"] = "", ["Espn:LeagueId"] = "0", ["Auth:Enabled"] = "true", ["AllowLan"] = "false" });
            builder.Services.AddSingleton<TimeProvider>(Clock);
            foreach (var descriptor in builder.Services.Where(d => d.ServiceType == typeof(IHostedService) && (d.ImplementationType == typeof(SyncWorker) || d.ImplementationType == typeof(Tank01Worker) || d.ImplementationType == typeof(EspnProjectionWorker))).ToList()) builder.Services.Remove(descriptor);
            builder.Services.RemoveAll<IHttpClientFactory>(); builder.Services.AddSingleton<IHttpClientFactory>(new NoNetworkClients(() => Interlocked.Increment(ref ExternalRequests)));
            builder.Logging.AddFilter(_ => false);
            var manifest = Environment.GetEnvironmentVariable("SUNDAY_RIVALRY_TEST_APP_DLL");
            if (manifest is not null)
            {
                builder.WebHost.UseSetting("staticWebAssets", Path.ChangeExtension(manifest, ".staticwebassets.runtime.json"));
                builder.WebHost.UseStaticWebAssets();
                builder.Configuration["StaticAssetsManifest"] = Path.ChangeExtension(manifest, ".staticwebassets.endpoints.json");
            }
        });
        await app.StartAsync();
        Address = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    }
    public async Task Restart()
    {
        await app!.StopAsync(); await app.DisposeAsync(); app = null; await Start();
    }
    public HttpClient Client(CookieContainer? cookies = null) => new(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies ?? new() }) { BaseAddress = Address };
    public async Task Advance(DateTimeOffset now) { Clock.Now = now; await Games.Mutate(_ => { }); }
    public async ValueTask DisposeAsync()
    {
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        if (ExternalRequests != 0) throw new Exception("Test attempted external network access.");
    }
    sealed class NoNetworkClients(Action attempted) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new DenyNetwork(attempted));
    }
    sealed class DenyNetwork(Action attempted) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { attempted(); throw new InvalidOperationException("External HTTP is blocked in the test host."); }
    }
}

static class SimulationData
{
    public static readonly DateTimeOffset Start = new(2030, 10, 10, 22, 0, 0, TimeSpan.Zero);
    public static AppState Create(DateTimeOffset now)
    {
        var state = new AppState { Names = ["Home player", "Away player"], LastSync = now, SyncMessage = "SIMULATED NFL FIXTURE · no external feeds" };
        var teams = new[] { "KC", "BUF", "SEA", "DEN", "CIN", "DAL" };
        var offsets = new[] { 2, 67, 99 }; var days = new[] { "Thursday", "Sunday", "Monday" };
        for (var game = 0; game < 3; game++) state.Games.Add(new() { Id = "sim-game-" + game, Season = now.Year, Week = 6, Away = teams[game * 2], Home = teams[game * 2 + 1], Kickoff = now.AddHours(offsets[game]), Day = days[game], Stadium = "Simulation Field" });
        foreach (var team in teams)
        foreach (var position in new[] { "QB", "RB", "RB", "RB", "WR", "WR", "WR", "TE", "K", "D/ST" }.Select((position, index) => (position, index)))
            state.Players.Add(new() { Id = position.position == "D/ST" ? "DST-" + team : $"sim-{team}-{position.index}", Name = $"Test {team} {position.position.Replace("/", "")} {position.index}", Team = team, Position = position.position, Status = "ACT" });
        return state;
    }
    public static StatLine Performance(Player player, int season, int week, bool live = false, DateTimeOffset? at = null)
    {
        var line = new StatLine { PlayerId = player.Id, Team = player.Team, Season = season, Week = week, IsLive = live, UpdatedAt = at };
        switch (player.Position)
        {
            case "QB": line.PassYards = 250; line.PassTd = 2; line.Interceptions = 1; line.RushYards = 20; line.FumblesLost = 1; line.TwoPoint = 1; break;
            case "RB": line.RushYards = 80; line.RushTd = 1; line.Receptions = 3; line.RecYards = 20; line.FumblesLost = 1; break;
            case "WR": line.Receptions = 6; line.RecYards = 90; line.RecTd = 1; break;
            case "TE": line.Receptions = 4; line.RecYards = 50; break;
            case "K": line.ExtraPoints = 2; line.FgShort = 1; line.Fg40 = 1; line.Fg50 = 1; break;
            case "D/ST": line.Defense = true; line.Sacks = 3; line.Takeaways = 2; line.DefenseTd = 1; line.Safeties = 1; line.Blocks = 1; line.PointsAllowed = 17; break;
        }
        return line;
    }
    public static double Expected(string position, double ppr) => position switch
    { "QB" => 18, "RB" => 14 + 3 * ppr, "WR" => 15 + 6 * ppr, "TE" => 5 + 4 * ppr, "K" => 14, "D/ST" => 18, _ => throw new ArgumentException(position) };
}
