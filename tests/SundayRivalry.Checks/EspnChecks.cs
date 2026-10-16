using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SundayRivalry.Domain;
using SundayRivalry.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SundayRivalry.Components.Shared;

static class EspnChecks
{
    public static async Task Run()
    {
        var clock = new TestClock { Now = SimulationData.Start }; var fixture = SimulationData.Create(clock.Now);
        var wr = fixture.Players.First(p => p.Team == "BUF" && p.Position == "WR"); var kicker = fixture.Players.First(p => p.Team == "BUF" && p.Position == "K");
        object Row(Player p, int position, Dictionary<string, double> stats, int week = 6, int source = 1, string? injury = "ACTIVE") => new { player = new { fullName = p.Name, defaultPositionId = position, proTeamId = 2, injuryStatus = injury, stats = new[] { new { seasonId = 2030, scoringPeriodId = week, statSourceId = source, statSplitTypeId = 1, appliedTotal = 9999, stats } } } };
        string Payload(params object[] rows) => JsonSerializer.Serialize(new { seasonId = 2030, total = rows.Length, players = rows });
        var receiver = Row(wr, 3, new() { ["53"] = 6, ["42"] = 80, ["43"] = 1, ["72"] = .5 }, injury: "QUESTIONABLE");
        var kicking = Row(kicker, 5, new() { ["74"] = 1, ["77"] = 1, ["80"] = 1, ["83"] = 3, ["86"] = 2 });
        var json = Payload(receiver, kicking, Row(fixture.Players.First(p => p.Team == "BUF" && p.Position == "QB"), 1, new() { ["53"] = 100, ["42"] = 800 }, 7));
        var parsed = EspnProjections.Parse(json, 2030, 6, fixture.Players);
        void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
        Check(parsed.Sets.Select(p => p.Players.Single(x => x.PlayerId == wr.Id).Points).SequenceEqual(new[] { 13d, 16, 19 }), "ESPN raw weekly categories calculate our standard, half and full PPR; league totals are ignored");
        Check(parsed.Sets.All(p => p.Players.Single(x => x.PlayerId == kicker.Id).Points == 14), "ESPN kicker distance categories use our scoring");
        Check(parsed.Injuries.Any(i => i.PlayerId == wr.Id && i.Designation == "Questionable") && parsed.Injuries.Any(i => i.PlayerId == kicker.Id && i.Designation == ""), "ESPN parses injury and healthy statuses independently from fantasy scoring");
        var noProjection = EspnProjections.Parse(Payload(Row(wr, 3, new(), injury: "INJURY_RESERVE")), 2030, 6, fixture.Players);
        Check(noProjection.Injuries.Single().Designation == "IR" && noProjection.Sets.All(s => s.Players.Count == 0), "IR designation is available even without a weekly projection");
        Check(EspnProjections.Designation("OUT", true) == "Out" && EspnProjections.Designation("DOUBTFUL", false) == "Doubtful" && EspnProjections.Designation("DAY_TO_DAY", false) == "Day-to-day" && EspnProjections.Designation(null, true) == "Injured" && EspnProjections.Designation(null, false) is null, "ESPN injury normalization distinguishes injury, active and unknown data");
        Check(EspnProjections.Parse(Payload(Row(wr, 3, new() { ["53"] = 6, ["42"] = 80, ["43"] = 1 }, 6, 0)), 2030, 6, fixture.Players).Sets.All(p => p.Players.Count == 0), "ESPN actual stats cannot become pregame projections");
        Check(EspnProjections.Parse(Payload(Row(wr, 3, new() { ["53"] = 6 }, 0)), 2030, 6, fixture.Players).Sets.All(p => p.Players.Count == 0), "Season totals and incomplete categories are not substituted for weekly projections");
        var duplicateRejected = false;
        try { EspnProjections.Parse(Payload(receiver, receiver), 2030, 6, fixture.Players); } catch (InvalidDataException) { duplicateRejected = true; }
        Check(duplicateRejected, "Ambiguous duplicate ESPN players cannot silently overwrite each other");
        var invalidSeason = false;
        try { EspnProjections.Parse(json, 2029, 6, fixture.Players); } catch (InvalidDataException) { invalidSeason = true; }
        Check(invalidSeason, "ESPN response must match the requested season");
        var directory = Path.GetFullPath(Path.Combine("artifacts", "espn-tests", Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(directory);
        var factory = new TestFactory(Path.Combine(directory, "test.db")); var store = new GameService(factory, clock); await store.Initialize();
        await store.Mutate(s => { s.Games = fixture.Games; s.Players = fixture.Players; });
        var id = await store.Create(2030, 6, "Salary", 1, 200, "Frozen before ESPN", fixture.Games.Select(g => g.Id).ToList());
        var oldSalary = GameService.Find(store.Snapshot(), id).Pool.Single(p => p.Player.Id == wr.Id).Salary;
        var oldProjection = GameService.Find(store.Snapshot(), id).Pool.Single(p => p.Player.Id == wr.Id).Projection;
        var handler = new Responses { Json = json }; var clients = new Clients(handler); var service = new EspnProjections(store, clients, clock);
        await Task.WhenAll(service.Refresh(123, 2030, 6), service.Refresh(123, 2030, 6));
        Check(handler.Calls == 1 && store.Snapshot().Projections.Count == 3 && store.Snapshot().Espn.LastSuccess == clock.Now, "ESPN shares one cached request across clients and all scoring formats");
        Check(store.Snapshot().Injuries.Single(i => i.PlayerId == wr.Id).Designation == "Questionable" && store.Snapshot().Espn.InjuriesUpdatedAt == clock.Now, "Successful ESPN refresh persists injury status and fetch time");
        var updated = GameService.Find(store.Snapshot(), id).Pool.Single(p => p.Player.Id == wr.Id);
        Check(updated.Projection == oldProjection && updated.Salary == oldSalary, "ESPN cache refresh preserves estimates and salaries already saved in a competition");
        var fresh = await store.Create(2030, 6, "Salary", 1, 200, "After ESPN", fixture.Games.Select(g => g.Id).ToList());
        var imported = GameService.Find(store.Snapshot(), fresh).Pool.Single(p => p.Player.Id == wr.Id);
        Check(imported.Projection == 19 && imported.ProjectionSource == EspnProjections.Source && imported.ProjectionImportedAt == clock.Now, "New competition receives ESPN points with source and fetch time");
        var snapshot = store.Snapshot();
        snapshot.Projections.Add(new() { Season = 2030, Week = 6, Ppr = 1, Source = "FanDuel Research", ImportedAt = clock.Now.AddMinutes(1), Players = [new() { PlayerId = wr.Id, Points = 99 }] });
        var preferred = GameService.BuildPool(fixture.Players, [], GameService.Find(snapshot, fresh), snapshot.Projections).Single(p => p.Player.Id == wr.Id);
        Check(preferred.Projection == 19 && preferred.ProjectionSource == EspnProjections.Source, "ESPN is preferred over a newer alternative projection source");
        var restarted = new GameService(factory, clock); await restarted.Initialize();
        await new EspnProjections(restarted, clients, clock).Refresh(123, 2030, 6);
        Check(handler.Calls == 1 && restarted.Snapshot().Projections.Count == 3 && restarted.Snapshot().Injuries.Any(i => i.Designation == "Questionable"), "ESPN cache, injuries and one-hour cooldown persist across service restart");
        clock.Now = clock.Now.AddHours(6); handler.Status = HttpStatusCode.Unauthorized;
        await service.Refresh(123, 2030, 6);
        Check(store.Snapshot().Projections.Count == 3 && store.Snapshot().Espn.Status.Contains("Public"), "Private ESPN league failure retains last good projections and explains setup");
        await service.Refresh(123, 2030, 6); Check(handler.Calls == 2, "Failed ESPN refresh has a retry cooldown");
        clock.Now = clock.Now.AddMinutes(5); handler.Status = HttpStatusCode.OK; handler.Json = "{\"unknown\":true}";
        await service.Refresh(123, 2030, 6);
        Check(store.Snapshot().Projections.Count == 3 && store.Snapshot().Espn.LastSuccess == SimulationData.Start, "ESPN schema failure preserves last successful cache and timestamp");
        clock.Now = clock.Now.AddMinutes(5); handler.Json = json; await service.Refresh(123, 2030, 6);
        Check(store.Snapshot().Espn.LastSuccess == clock.Now && store.Snapshot().Projections.Count == 3, "Valid ESPN refresh recovers without accumulating duplicate projection sets");
        var originalSalary = GameService.Find(store.Snapshot(), id).Pool.Single(p => p.Player.Id == wr.Id).Salary;
        clock.Now = clock.Now.AddHours(1); handler.Json = Payload(Row(wr, 3, new(), injury: "ACTIVE")); await service.Refresh(123, 2030, 6);
        Check(store.Snapshot().Injuries.Single(i => i.PlayerId == wr.Id).Designation == "" && store.Snapshot().Projections.Count == 3, "Active ESPN status clears an injury badge even when no projected stats are available");
        Check(GameService.Find(store.Snapshot(), id).Pool.Single(p => p.Player.Id == wr.Id).Salary == originalSalary, "Injury metadata updates do not reprice a saved draft");
        clock.Now = clock.Now.AddHours(1); handler.Json = Payload(Row(wr, 3, new(), injury: null)); await service.Refresh(123, 2030, 6);
        Check(store.Snapshot().Injuries.Single(i => i.PlayerId == wr.Id).UpdatedAt == clock.Now.AddHours(-1), "Missing ESPN injury status does not invent a fresh healthy report");
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        async Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent => await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
        var uiState = new AppState { Injuries = [new() { PlayerId = wr.Id, Designation = "IR", UpdatedAt = clock.Now }] };
        var html = await Render<InjuryBadge>(new() { ["Player"] = wr, ["State"] = uiState });
        Check(html.Contains("injury-unavailable") && html.Contains(">IR</span>") && html.Contains("ESPN") && html.Contains("UTC"), "Injury badge renders explicit IR label, severity, source and fetch time");
        uiState.Injuries[0].Designation = "";
        html = await Render<InjuryBadge>(new() { ["Player"] = wr, ["State"] = uiState });
        Check(!html.Contains("injury-badge"), "Healthy players have no injury badge");
        uiState.Injuries[0].Designation = "Questionable";
        var privateRoom = new Competition { Format = "Salary", Names = ["One", "Two"], Games = [new() { Kickoff = DateTimeOffset.UtcNow.AddHours(2) }], Pool = [new() { Player = wr }], Picks = [new() { Owner = 0, PlayerId = wr.Id, Slot = "WR1" }] };
        html = await Render<Roster>(new() { ["State"] = uiState, ["C"] = privateRoom, ["Owner"] = 0, ["Viewer"] = 1 });
        Check(!html.Contains("injury-badge") && !html.Contains(wr.Name), "Injury display cannot reveal a private opponent salary roster");
        privateRoom.Pool[0].Projection = 18; privateRoom.Pool[0].Salary = 40;
        foreach (var source in new[] { "", EspnProjections.Source })
        foreach (var designation in new[] { "Out", "IR" })
        {
            privateRoom.Pool[0].ProjectionSource = source; uiState.Injuries[0].Designation = designation;
            Check(GameService.EstimatedPoints(uiState, privateRoom, privateRoom.Pool[0]) == 0 && GameService.Total(uiState, privateRoom, 0, true) == 0 && GameService.EstimateLabel(uiState, privateRoom, privateRoom.Pool[0]).Contains("0 projected"), $"{designation} overrides both ESPN and historical estimates, including team totals");
        }
        html = await Render<Roster>(new() { ["State"] = uiState, ["C"] = privateRoom, ["Owner"] = 0, ["Viewer"] = 0 });
        Check(html.Contains("class=\"roster-points\">0.0</span>"), "IR player's rendered roster projection is zero");
        uiState.Stats.Add(new() { PlayerId = wr.Id, Receptions = 6, RecYards = 80, RecTd = 1 });
        Check(GameService.Total(uiState, privateRoom, 0, false) == 20, "Unavailable designation does not erase actual earned fantasy points");
        foreach (var designation in new[] { "Questionable", "Doubtful", "" })
        {
            uiState.Injuries[0].Designation = designation;
            Check(GameService.EstimatedPoints(uiState, privateRoom, privateRoom.Pool[0]) == 18 && privateRoom.Pool[0].Salary == 40, "Non-out designation restores the saved estimate without repricing");
        }
    }

    public static async Task Preview()
    {
        using var db = new SqliteConnection("Data Source=src/SundayRivalry/App_Data/rivalry.db;Mode=ReadOnly;Pooling=False"); await db.OpenAsync();
        using var command = db.CreateCommand(); command.CommandText = "SELECT Json FROM State WHERE Id=1";
        var state = JsonSerializer.Deserialize<AppState>((string)(await command.ExecuteScalarAsync())!)!;
        Console.WriteLine(JsonSerializer.Serialize(new { injuryStatuses = state.Injuries.GroupBy(i => i.Designation.Length == 0 ? "Healthy" : i.Designation).Select(g => new { designation = g.Key, players = g.Count() }), competitionFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state.Competitions)))) }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { competitions = state.Competitions.Select(c => new { c.Id, c.Title, c.Format, c.Season, c.Week, picks = c.Picks.Count }), upcomingGames = state.Games.Where(g => g.Kickoff > DateTimeOffset.UtcNow).OrderBy(g => g.Kickoff).Take(18).Select(g => new { g.Id, g.Day, g.Home, g.Away, g.Kickoff }) }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { savedConnection = state.Espn, cachedSets = state.Projections.Where(s => s.Source == EspnProjections.Source).Select(s => new { s.Season, s.Week, s.Ppr, players = s.Players.Count, s.ImportedAt }), openCompetitions = state.Competitions.Where(c => !c.Demo && !c.Locked).Select(c => new { c.Season, c.Week, espnEstimates = c.Pool.Count(p => p.ProjectionSource == EspnProjections.Source), localEstimates = c.Pool.Count(p => p.ProjectionSource.Length == 0) }) }, new JsonSerializerOptions { WriteIndented = true }));
        var result = EspnProjections.Parse(await File.ReadAllTextAsync("artifacts/espn-research/league-response.json"), 2026, 6, state.Players);
        Console.WriteLine(JsonSerializer.Serialize(new { sets = result.Sets.Select(s => new { s.Ppr, players = s.Players.Count }), issues = result.Issues.Take(12) }, new JsonSerializerOptions { WriteIndented = true }));
    }
    sealed class Clients(Responses handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, false); }
    sealed class Responses : HttpMessageHandler
    {
        public string Json = ""; public int Calls; public HttpStatusCode Status = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; if (request.RequestUri!.Host != "lm-api-reads.fantasy.espn.com" || !request.Headers.Contains("x-fantasy-filter")) throw new Exception("Incorrect ESPN request"); return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Json) }); }
    }
}
