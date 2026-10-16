using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SundayRivalry.Domain;
using SundayRivalry.Services;

static class Tank01Checks
{
    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SundayRivalryChecks", "tank01-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var clock = new TestClock(); var paths = new LiveFeedPaths(directory); var ledger = new Tank01Ledger(paths, clock);
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAILED " + label); Console.WriteLine("PASS " + label); checks++; }
        Check(ledger.Reserve("one", out _) && !ledger.Reserve("one", out _) && ledger.Usage().Used == 1, "A request is reserved before sending; repeated polling does not spend another call");
        var restartedLedger = new Tank01Ledger(paths, clock);
        Check(!restartedLedger.Reserve("one", out _) && restartedLedger.Usage().Used == 1, "Request counts and cooldown survive ledger restart");
        clock.Now = clock.Now.AddMinutes(31);
        Check(restartedLedger.Reserve("one", out _), "A game can be fetched again after the polling interval");
        var quotaPaths = new LiveFeedPaths(Path.Combine(directory, "quota")); var quota = new Tank01Ledger(quotaPaths, clock); _ = quota.Usage();
        using (var db = new SqliteConnection($"Data Source={Path.Combine(quotaPaths.Directory, "tank01-usage.db")}"))
        {
            db.Open(); using var tx = db.BeginTransaction();
            for (var i = 0; i < 899; i++)
            {
                using var cmd = db.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO Requests VALUES($id,$at,$resource)";
                cmd.Parameters.AddWithValue("$id", i.ToString()); cmd.Parameters.AddWithValue("$at", clock.Now.AddDays(-1 - i / 100).ToUniversalTime().ToString("O")); cmd.Parameters.AddWithValue("$resource", "seed-" + i); cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        var reservations = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => new Tank01Ledger(quotaPaths, clock).Reserve("race-" + i, out _))));
        Check(reservations.Count(x => x) == 1 && quota.Usage().Used == 900, "Concurrent app instances cannot exceed the 900-request rolling cap");
        clock.Now = clock.Now.AddDays(1);
        Check(!quota.Reserve("next-month", out _), "Calendar-day/month changes do not reset the rolling allowance");
        clock.Now = clock.Now.AddDays(32);
        Check(quota.Reserve("aged-out", out _), "Calls resume when old requests actually age out of the 31-day window");
        var daily = new Tank01Ledger(new LiveFeedPaths(Path.Combine(directory, "daily")), clock);
        for (var i = 0; i < 100; i++) CheckReserve(daily, "daily-" + i);
        Check(!daily.Reserve("daily-overflow", out _) && daily.Usage().Today == 100, "Daily safety budget stops at 100 attempts");
        ledger.Block(clock.Now.AddDays(31), "Quota guard fixture");
        Check(!new Tank01Ledger(paths, clock).Reserve("blocked", out _), "Provider quota stop persists across restarts");

        var appPaths = new LiveFeedPaths(Path.Combine(directory, "app")); Directory.CreateDirectory(appPaths.Directory);
        var store = new GameService(new TestFactory(Path.Combine(appPaths.Directory, "games.db")), clock); await store.Initialize();
        var game = new NflGame { Id = "test-live-game", Away = "KC", Home = "BUF", Season = 2026, Week = 6, Kickoff = clock.Now.AddHours(-1) };
        var player = new Player { Id = "local-qb", Name = "Test Quarterback", Team = "KC", Position = "QB" };
        var competition = new Competition { Games = [game], Pool = [new() { Player = player, GameId = game.Id, Kickoff = game.Kickoff }], Picks = [new() { PlayerId = player.Id, Owner = 0, Slot = "QB" }], Season = game.Season, Week = game.Week };
        await store.Mutate(s => { s.Players = [player]; s.Games = [game]; s.Competitions = [competition, competition]; });
        Check(Tank01Live.RelevantGames(store.Snapshot(), clock.Now).Count == 1, "Multiple competitions share one relevant game fetch");
        Check(Tank01Live.RelevantGames(store.Snapshot(), game.Kickoff.AddSeconds(-1)).Count == 0 && Tank01Live.RelevantGames(store.Snapshot(), game.Kickoff.AddHours(7)).Count == 0, "No requests before kickoff or beyond the six-hour game window");
        var handler = new FixtureHandler(); var clientFactory = new FixtureClients(handler);
        var kickerJson = """{"statusCode":200,"body":{"away":"KC","home":"BUF","playerStats":{"k":{"longName":"Test Kicker","team":"KC","Kicking":{"xpMade":"0","fgMade":"2","fgLong":"55"},"scoringPlays":[{"scoreType":"FG","score":"Test Kicker 55 Yd Field Goal","scoreTime":"6:31","scorePeriod":"Q1"},{"scoreType":"FG","score":"Test Kicker 34 Yd Field Goal","scoreTime":"0:03","scorePeriod":"Q4"}]}}}}""";
        var kicker = new Player { Id = "local-k", Name = "Test Kicker", Team = "KC", Position = "K" };
        var kickStats = Tank01Parser.BoxScore(kickerJson, game, "fixture-id", [kicker], clock.Now).Stats.Single();
        Check(kickStats.FgShort == 1 && kickStats.Fg50 == 1 && kickStats.Points(1) == 8 && !kickStats.CoverageIssues.Any(i => i.StartsWith("Field goals")), "Kicker distance scoring uses reconciled made-FG scoring plays");
        var incompleteKicks = Tank01Parser.BoxScore(kickerJson.Replace("\"fgMade\":\"2\"", "\"fgMade\":\"3\""), game, "fixture-id", [kicker], clock.Now).Stats.Single();
        Check(incompleteKicks.CoverageIssues.Any(i => i.StartsWith("Field goals")), "Unreconciled kicking plays stay explicitly partial instead of guessing distances");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Tank01:ApiKey"] = "synthetic-test-key-not-a-real-credential" }).Build();
        var appLedger = new Tank01Ledger(appPaths, clock);
        Tank01Live Service(IConfiguration settings) => new(store, appLedger, clientFactory, settings, appPaths, clock, NullLogger<Tank01Live>.Instance);
        await Service(new ConfigurationBuilder().Build()).Poll();
        Check(handler.Calls == 0 && appLedger.Usage().Used == 0, "Missing key makes zero network requests");
        var live = Service(config); await live.Poll();
        Check(handler.Calls == 2 && appLedger.Usage().Used == 2 && store.Snapshot().LiveScores.Count == 1, "Schedule discovery and first box score each consume one counted request");
        var actual = GameService.Actual(store.Snapshot(), competition, player.Id);
        Check(actual is { IsLive: true } && actual.Points(1) == 18 && actual.CoverageIssues.Count > 0, "Known live stats use local scoring and flag omitted categories as partial");
        await store.Mutate(s => { s.LastSync = clock.Now; s.Stats = [new() { PlayerId = player.Id, Season = game.Season, Week = game.Week }]; });
        Check(GameService.Actual(store.Snapshot(), competition, player.Id)?.Points(1) == 18, "Delayed historical imports do not overwrite fresh in-game scores");
        await live.Poll(); await Service(config).Poll();
        Check(handler.Calls == 2, "Refreshes and live-service restarts cannot bypass game cooldown");
        await store.Mutate(s => s.LivePollingPaused = true); clock.Now = clock.Now.AddMinutes(31); await live.Poll();
        Check(handler.Calls == 2, "League pause sends no API requests");
        await store.Mutate(s => s.LivePollingPaused = false); handler.Broken = true; await live.Poll();
        Check(handler.Calls == 3 && store.Snapshot().LiveScores.Single().Stats.Single().PassYards == 250, "Unrecognized provider payload preserves the last valid score");
        handler.Broken = false; handler.Final = true; clock.Now = clock.Now.AddMinutes(31); await live.Poll();
        Check(store.Snapshot().LiveScores.Single().Final, "A completed game stops future polling");
        clock.Now = clock.Now.AddMinutes(31); await live.Poll(); Check(handler.Calls == 4, "Completed game is not polled again");
        await store.Mutate(s => { s.LiveScores.Clear(); s.Tank01GameIds.Clear(); }); handler.Limited = true; clock.Now = clock.Now.AddMinutes(31); await live.Poll();
        var count = handler.Calls; await Service(config).Poll();
        Check(handler.Calls == count && appLedger.Usage().BlockedUntil > clock.Now, "HTTP 429 disables further calls even after service restart");
        Console.WriteLine($"\n{checks} Tank01 safety and scoring checks passed (mocked HTTP only).");
    }
    static void CheckReserve(Tank01Ledger ledger, string resource) { if (!ledger.Reserve(resource, out var reason)) throw new Exception(reason); }
    sealed class FixtureClients(FixtureHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    sealed class FixtureHandler : HttpMessageHandler
    {
        public int Calls; public bool Final, Broken, Limited;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Limited) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            var json = request.RequestUri!.AbsolutePath.Contains("GamesForDate")
                ? """{"statusCode":200,"body":[{"gameID":"DATE_KC@BUF","away":"KC","home":"BUF"}]}""".Replace("DATE", request.RequestUri.Query.Split("gameDate=")[1][..8])
                : Broken ? """{"statusCode":200,"body":{"unknown":"schema"}}"""
                : """{"statusCode":200,"body":{"away":"KC","home":"BUF","awayPts":"7","homePts":"3","gameStatus":"STATUS","playerStats":{"qb":{"longName":"Test Quarterback","team":"KC","Passing":{"passYds":"250","passTD":"2","int":"1"},"Rushing":{"rushYds":"20","rushTD":"0"}}}}}""".Replace("STATUS", Final ? "Completed" : "In Progress");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
