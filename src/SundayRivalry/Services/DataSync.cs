using System.Globalization;
using Microsoft.VisualBasic.FileIO;
using SundayRivalry.Domain;

namespace SundayRivalry.Services;

public class DataSync(GameService store, IHttpClientFactory clients, ILogger<DataSync> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public bool Busy { get; private set; }
    public async Task Sync(int season)
    {
        if (!await gate.WaitAsync(0)) return;
        Busy = true;
        try
        {
            var http = clients.CreateClient("nflverse");
            var games = await http.GetStringAsync("https://raw.githubusercontent.com/nflverse/nfldata/master/data/games.csv");
            var roster = await http.GetStringAsync($"https://github.com/nflverse/nflverse-data/releases/download/rosters/roster_{season}.csv");
            var parsedGames = ParseGames(games).Where(g => g.Season == season || g.Season == season - 1).ToList();
            var parsedPlayers = ParsePlayers(roster);
            var stats = new List<StatLine>();
            var warnings = new List<string>();
            foreach (var year in new[] { season - 1, season })
            {
                try
                {
                    var csv = await http.GetStringAsync($"https://github.com/nflverse/nflverse-data/releases/download/stats_player/stats_player_week_{year}.csv");
                    stats.AddRange(ParseStats(csv));
                }
                catch (HttpRequestException ex) { logger.LogWarning(ex, "Player stats unavailable for {Year}", year); warnings.Add($"{year} player statistics unavailable"); }
                try
                {
                    var csv = await http.GetStringAsync($"https://github.com/nflverse/nflverse-data/releases/download/stats_team/stats_team_week_{year}.csv");
                    stats.AddRange(ParseTeamStats(csv, parsedGames));
                }
                catch (HttpRequestException ex) { logger.LogWarning(ex, "Team stats unavailable for {Year}", year); warnings.Add($"{year} defense statistics unavailable"); }
            }
            await Apply(parsedGames, parsedPlayers, stats, warnings);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Football sync failed");
            await store.Mutate(s => s.SyncMessage = "Couldn't reach the football feed. Your saved games and drafts are safe. Try Sync again when connected.");
        }
        finally { Busy = false; gate.Release(); }
    }

    public async Task ImportDirectory(string path)
    {
        var games = ParseGames(await File.ReadAllTextAsync(Path.Combine(path, "games.csv")));
        var players = ParsePlayers(await File.ReadAllTextAsync(Path.Combine(path, "rosters.csv")));
        var stats = ParseStats(await File.ReadAllTextAsync(Path.Combine(path, "stats.csv")));
        stats.AddRange(ParseTeamStats(await File.ReadAllTextAsync(Path.Combine(path, "teamstats.csv")), games));
        await Apply(games, players, stats, []);
    }

    private async Task Apply(List<NflGame> games, List<Player> players, List<StatLine> stats, List<string> warnings)
    {
        if (games.Count == 0 || players.Count == 0) throw new InvalidDataException("The feed contained no usable games or players.");
        await store.Mutate(s =>
        {
            var years = games.Select(g => g.Season).Distinct().ToHashSet();
            s.Games = s.Games.Where(g => !years.Contains(g.Season)).Concat(games).DistinctBy(g => g.Id).ToList();
            s.Players = players;
            // Upsert, rather than append: reimports apply statistical corrections.
            s.Stats = stats.Concat(s.Stats).DistinctBy(x => (x.PlayerId, x.Season, x.Week)).ToList();
            s.LastSync = DateTimeOffset.UtcNow;
            s.SyncMessage = warnings.Count == 0 ? "NFL data imported. Statistics are delayed; estimates are generated locally." : string.Join("; ", warnings) + ". Cached statistics retained.";
        });
    }

    public static List<Dictionary<string, string>> Csv(string text)
    {
        using var parser = new TextFieldParser(new StringReader(text)) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");
        var headers = parser.ReadFields() ?? throw new InvalidDataException("CSV has no header.");
        headers[0] = headers[0].TrimStart('\uFEFF');
        var rows = new List<Dictionary<string, string>>();
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields();
            if (fields is null) continue;
            if (fields.Length != headers.Length) throw new InvalidDataException("CSV row does not match its header.");
            rows.Add(headers.Select((h, i) => (h, fields[i])).ToDictionary(x => x.h, x => x.Item2));
        }
        return rows;
    }
    private static string V(Dictionary<string, string> r, string key) => r.GetValueOrDefault(key, "");
    private static double N(Dictionary<string, string> r, string key) => double.TryParse(V(r, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    private static int? Score(Dictionary<string, string> r, string key) => int.TryParse(V(r, key), out var x) ? x : null;
    public static List<NflGame> ParseGames(string csv)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var games = new List<NflGame>();
        foreach (var r in Csv(csv).Where(r => V(r, "game_type") == "REG"))
        {
            if (!DateTime.TryParseExact($"{V(r, "gameday")} {V(r, "gametime")}", "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) continue;
            games.Add(new() { Id = V(r, "game_id"), Season = (int)N(r, "season"), Week = (int)N(r, "week"), Away = V(r, "away_team"), Home = V(r, "home_team"),
                Kickoff = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone), TimeSpan.Zero),
                Day = V(r, "weekday"), Stadium = V(r, "stadium"), HomeScore = Score(r, "home_score"), AwayScore = Score(r, "away_score") });
        }
        return games;
    }
    public static List<Player> ParsePlayers(string csv)
    {
        var players = Csv(csv).Where(r => Rules.Positions.Contains(V(r, "position")) && V(r, "gsis_id") != "" && V(r, "team") != "" && V(r, "status") is "ACT" or "INA" or "RES")
            .Select(r => new Player { Id = V(r, "gsis_id"), Name = V(r, "full_name"), Position = V(r, "position"), Team = V(r, "team"), Status = V(r, "status"), Photo = V(r, "headshot_url") })
            .DistinctBy(p => p.Id).ToList();
        foreach (var team in players.Select(p => p.Team).Distinct().ToList()) players.Add(new() { Id = $"DST-{team}", Name = $"{team} Defense", Position = "D/ST", Team = team, Status = "ACT" });
        return players;
    }
    public static List<StatLine> ParseStats(string csv) => Csv(csv).Where(r => V(r, "season_type") == "REG" && V(r, "player_id") != "").Select(r => new StatLine
    {
        PlayerId = V(r, "player_id"), Season = (int)N(r, "season"), Week = (int)N(r, "week"), Team = V(r, "team"),
        PassYards = N(r, "passing_yards"), PassTd = N(r, "passing_tds"), Interceptions = N(r, "passing_interceptions"),
        RushYards = N(r, "rushing_yards"), RushTd = N(r, "rushing_tds"), Receptions = N(r, "receptions"), RecYards = N(r, "receiving_yards"), RecTd = N(r, "receiving_tds"),
        FumblesLost = N(r, "fumbles_lost_total"), TwoPoint = N(r, "passing_2pt_conversions") + N(r, "rushing_2pt_conversions") + N(r, "receiving_2pt_conversions"),
        ExtraPoints = N(r, "pat_made"), FgShort = N(r, "fg_made_0_19") + N(r, "fg_made_20_29") + N(r, "fg_made_30_39"),
        Fg40 = N(r, "fg_made_40_49"), Fg50 = N(r, "fg_made_50_59") + N(r, "fg_made_60_"), ReturnTd = N(r, "special_teams_tds")
    }).DistinctBy(x => (x.PlayerId, x.Season, x.Week)).ToList();

    public static List<StatLine> ParseTeamStats(string csv, List<NflGame> games)
    {
        var result = new List<StatLine>();
        foreach (var r in Csv(csv).Where(r => V(r, "season_type") == "REG"))
        {
            var game = games.FirstOrDefault(g => g.Id == V(r, "game_id"));
            if (game is null || !game.HomeScore.HasValue || !game.AwayScore.HasValue) continue;
            var team = V(r, "team");
            result.Add(new() { PlayerId = $"DST-{team}", Team = team, Season = (int)N(r, "season"), Week = (int)N(r, "week"), Defense = true,
                Sacks = N(r, "def_sacks"), Takeaways = N(r, "def_interceptions") + N(r, "fumble_recovery_opp"), DefenseTd = N(r, "def_tds") + N(r, "special_teams_tds"),
                Safeties = N(r, "def_safeties"), Blocks = N(r, "def_punt_blocks") + N(r, "def_fg_blocks") + N(r, "def_pat_blocks"), PointsAllowed = (game.Home == team ? game.AwayScore : game.HomeScore)!.Value });
        }
        return result.DistinctBy(x => (x.PlayerId, x.Season, x.Week)).ToList();
    }
}

public class SyncWorker(DataSync sync, GameService store, ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try
            {
                var state = store.Snapshot();
                if (state.LastSync is null || DateTimeOffset.UtcNow - state.LastSync > TimeSpan.FromHours(12)
                    || state.Competitions.Any(c => !c.Demo && c.Locked && !GameService.Final(state, c)))
                    await sync.Sync(DateTimeOffset.UtcNow.Year);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Background sync failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
