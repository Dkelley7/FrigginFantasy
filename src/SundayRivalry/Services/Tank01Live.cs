using System.Net;
using SundayRivalry.Domain;

namespace SundayRivalry.Services;

public sealed class Tank01Live(GameService store, Tank01Ledger ledger, IHttpClientFactory clients,
    IConfiguration configuration, LiveFeedPaths paths, TimeProvider clock, ILogger<Tank01Live> logger)
{
    const string Host = "tank01-nfl-live-in-game-real-time-statistics-nfl.p.rapidapi.com";
    readonly SemaphoreSlim gate = new(1, 1);
    public bool Configured => !string.IsNullOrWhiteSpace(configuration["Tank01:ApiKey"]);
    public Tank01Usage Usage => ledger.Usage();
    public static List<NflGame> RelevantGames(AppState state, DateTimeOffset now)
    {
        var games = new Dictionary<string, NflGame>();
        foreach (var competition in state.Competitions.Where(c => !c.Demo))
        {
            var ids = competition.Picks.Select(p => competition.Pool.FirstOrDefault(x => x.Player.Id == p.PlayerId)?.GameId).ToHashSet();
            foreach (var original in competition.Games.Where(g => ids.Contains(g.Id)))
            {
                var game = state.Games.FirstOrDefault(g => g.Id == original.Id) ?? original;
                if (game.Kickoff <= now && now <= game.Kickoff.AddHours(6) && !state.LiveScores.Any(s => s.GameId == game.Id && s.Final)) games[game.Id] = game;
            }
        }
        return games.Values.OrderBy(g => g.Kickoff).ThenBy(g => g.Id).ToList();
    }
    async Task Status(string message)
    {
        if (store.Snapshot().LiveStatus != message) await store.Mutate(s => s.LiveStatus = message);
    }
    async Task<string?> Fetch(string route, string resource, CancellationToken cancellationToken)
    {
        if (!ledger.Reserve(resource, out var reason)) { await Status(reason); return null; }
        // Every call, including errors, is already counted. No redirects/retries.
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://" + Host + route);
        request.Headers.Add("X-RapidAPI-Key", configuration["Tank01:ApiKey"]);
        request.Headers.Add("X-RapidAPI-Host", Host);
        try
        {
            using var response = await clients.CreateClient("tank01").SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                ledger.Block(clock.GetUtcNow().AddDays(31), "Tank01 reported a rate/quota limit. Automatic calls are paused for 31 days to protect the free allowance.");
                await Status("Tank01 rate/quota limit reached. Calls paused; saved scores retained."); return null;
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                ledger.Block(clock.GetUtcNow().AddHours(1), "Tank01 rejected the key or subscription. Check the free NFL subscription; calls are paused for one hour.");
                await Status("Tank01 key/subscription rejected. Calls paused for one hour; saved scores retained."); return null;
            }
            if (response.Headers.TryGetValues("x-ratelimit-requests-remaining", out var remaining)
                && long.TryParse(remaining.FirstOrDefault(), out var allowance) && allowance <= 100)
                ledger.Block(clock.GetUtcNow().AddDays(31), "Provider allowance is at or below the 100-request reserve. Automatic calls are paused for 31 days.");
            if (!response.IsSuccessStatusCode) { await Status($"Tank01 returned HTTP {(int)response.StatusCode}. No immediate retry; saved scores retained."); return null; }
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { await Status("Tank01 timed out. The attempt was counted; no immediate retry."); return null; }
        catch (HttpRequestException)
        { await Status("Tank01 could not be reached. The attempt was counted; no immediate retry."); return null; }
    }
    public async Task<string> Validate(CancellationToken cancellationToken = default)
    {
        if (!Configured) return "No Tank01 key configured; no requests sent.";
        var state = store.Snapshot();
        var game = state.Games.Where(g => g.Kickoff < clock.GetUtcNow().AddHours(-6)).OrderByDescending(g => g.Kickoff).FirstOrDefault();
        if (game is null) return "No completed schedule candidate available; no requests sent.";
        var date = TimeZoneInfo.ConvertTime(game.Kickoff, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).ToString("yyyyMMdd");
        var schedule = await Fetch("/getNFLGamesForDate?gameDate=" + date, "schedule:" + date, cancellationToken);
        if (schedule is null) return store.Snapshot().LiveStatus;
        var archive = Path.Combine(paths.Directory, "tank01-responses"); Directory.CreateDirectory(archive);
        await File.WriteAllTextAsync(Path.Combine(archive, "validation-schedule.json"), schedule, cancellationToken);
        var mapping = Tank01Parser.Schedule(schedule, [game]);
        if (!mapping.TryGetValue(game.Id, out var providerId)) return "Provider schedule did not match the validation game. No guessed box-score call sent.";
        await store.Mutate(s => s.Tank01GameIds[game.Id] = providerId);
        var json = await Fetch("/getNFLBoxScore?gameID=" + Uri.EscapeDataString(providerId) + "&fantasyStats=false", "box:" + game.Id, cancellationToken);
        if (json is null) return store.Snapshot().LiveStatus;
        await File.WriteAllTextAsync(Path.Combine(archive, "validation-boxscore.json"), json, cancellationToken);
        var result = Tank01Parser.BoxScore(json, game, providerId, state.Players, clock.GetUtcNow());
        return $"Tank01 authenticated; validated {game.Away} at {game.Home}: {result.Stats.Count} matched stat lines, {result.Stats.Count(s => s.CoverageIssues.Count > 0)} partial lines. Both validation requests were counted. No league scores were changed.";
    }
    public async Task Poll(CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) return;
        try
        {
            if (!Configured) { await Status("Tank01 is not configured. Run Configure-Tank01.cmd on the desktop to add a free-plan key."); return; }
            var state = store.Snapshot();
            if (state.LivePollingPaused) { await Status("Live updates paused by a league participant. No Tank01 requests are being sent."); return; }
            var games = RelevantGames(state, clock.GetUtcNow());
            if (games.Count == 0) { await Status("Waiting for a rostered player's game window. No Tank01 requests outside kickoff through six hours afterward."); return; }
            var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            foreach (var dateGroup in games.GroupBy(g => TimeZoneInfo.ConvertTime(g.Kickoff, zone).ToString("yyyyMMdd")))
            {
                var missing = dateGroup.Where(g => !state.Tank01GameIds.ContainsKey(g.Id)).ToList();
                if (missing.Count > 0)
                {
                    var json = await Fetch("/getNFLGamesForDate?gameDate=" + dateGroup.Key, "schedule:" + dateGroup.Key, cancellationToken);
                    if (json is not null)
                    {
                        try
                        {
                            var mappings = Tank01Parser.Schedule(json, dateGroup);
                            await store.Mutate(s => { foreach (var (local, provider) in mappings) s.Tank01GameIds[local] = provider; });
                            state = store.Snapshot();
                            if (mappings.Count == 0) await Status("Tank01 schedule format or teams could not be matched. No guessed box-score requests were sent.");
                        }
                        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
                        { await Status("Tank01 schedule could not be validated. Saved data retained; retry waits at least 30 minutes."); }
                    }
                }
                foreach (var game in dateGroup)
                {
                    // Recheck after potentially slow network calls or a participant's pause.
                    state = store.Snapshot();
                    if (state.LivePollingPaused || !RelevantGames(state, clock.GetUtcNow()).Any(g => g.Id == game.Id)) continue;
                    if (!state.Tank01GameIds.TryGetValue(game.Id, out var providerId)) continue;
                    var json = await Fetch("/getNFLBoxScore?gameID=" + Uri.EscapeDataString(providerId) + "&fantasyStats=false", "box:" + game.Id, cancellationToken);
                    if (json is null) continue;
                    var archive = Path.Combine(paths.Directory, "tank01-responses"); Directory.CreateDirectory(archive);
                    // Use the local game's safe hash for filenames, never a URL or key.
                    var filename = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(game.Id)));
                    await File.WriteAllTextAsync(Path.Combine(archive, filename + ".json"), json, cancellationToken);
                    try
                    {
                        var players = state.Competitions.Where(c => !c.Demo).SelectMany(c => c.Pool).Select(p => p.Player).Concat(state.Players).DistinctBy(p => (p.Id, p.Team)).ToList();
                        var score = Tank01Parser.BoxScore(json, game, providerId, players, clock.GetUtcNow());
                        await store.Mutate(s =>
                        {
                            s.LiveScores.RemoveAll(b => b.GameId == game.Id); s.LiveScores.Add(score);
                            s.LiveStatus = score.Issues.Count == 0 ? "Tank01 box score updated. Scores remain provisional." : "Tank01 box score updated with partial coverage. See scoring details for missing categories.";
                        });
                    }
                    catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
                    { await Status("Tank01 box-score format could not be validated. Saved scores retained; raw response saved locally for inspection."); }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // No exception payloads/headers/credentials in logs.
            logger.LogWarning("Tank01 update stopped safely ({ExceptionType}); no automatic retry", ex.GetType().Name);
            await Status("Live update stopped safely. Saved scores retained; check desktop logs.");
        }
        finally { gate.Release(); }
    }
}

public sealed class Tank01Worker(Tank01Live live) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            do { await live.Poll(stoppingToken); } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
