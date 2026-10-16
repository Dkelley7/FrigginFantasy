using System.Globalization;
using System.Net;
using System.Text.Json;
using SundayRivalry.Domain;

namespace SundayRivalry.Services;

public sealed class EspnProjections(GameService store, IHttpClientFactory clients, TimeProvider clock)
{
    public const string Source = "ESPN · our scoring";
    readonly SemaphoreSlim gate = new(1, 1);
    public async Task Refresh(long leagueId, int season, int week, CancellationToken cancellationToken = default)
    {
        if (leagueId <= 0 || season is < 2020 or > 2100 || week is < 1 or > 18) throw new InvalidOperationException("Enter a positive ESPN league ID, valid season and NFL week 1–18.");
        if (!await gate.WaitAsync(0, cancellationToken)) return;
        try
        {
            var prior = store.Snapshot().Espn;
            var needsInjuries = prior.LastSuccess >= prior.LastAttempt && prior.InjuriesUpdatedAt is null;
            if (!needsInjuries && prior.LeagueId == leagueId && prior.Season == season && prior.Week == week && prior.LastAttempt is { } attempted && clock.GetUtcNow() - attempted < (prior.LastSuccess >= attempted ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(5))) return;
            await store.Mutate(s => { s.Espn.LeagueId = leagueId; s.Espn.Season = season; s.Espn.Week = week; s.Espn.LastAttempt = clock.GetUtcNow(); s.Espn.Status = "Refreshing ESPN weekly projections…"; });
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://lm-api-reads.fantasy.espn.com/apis/v3/games/ffl/seasons/{season}/segments/0/leagues/{leagueId}?view=kona_player_info&scoringPeriodId={week}");
            request.Headers.Add("x-fantasy-filter", "{\"players\":{\"limit\":2000,\"sortPercOwned\":{\"sortPriority\":1,\"sortAsc\":false}}}");
            using var response = await clients.CreateClient("espn-projections").SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new InvalidDataException("ESPN denied access. Set Make League Viewable to Public to Yes and verify the season. No ESPN cookies are needed.");
            response.EnsureSuccessStatusCode();
            var parsed = Parse(await response.Content.ReadAsStringAsync(cancellationToken), season, week, store.Snapshot().Players);
            await store.Mutate(s =>
            {
                foreach (var set in parsed.Sets.Where(set => set.Players.Count > 0))
                {
                    set.ImportedAt = clock.GetUtcNow(); foreach (var player in set.Players) player.ImportedAt = set.ImportedAt;
                    s.Projections.RemoveAll(p => p.Source == Source && p.Season == season && p.Week == week && p.Ppr == set.Ppr);
                    s.Projections.Add(set);
                }
                foreach (var injury in parsed.Injuries)
                {
                    injury.UpdatedAt = clock.GetUtcNow();
                    s.Injuries.RemoveAll(i => i.PlayerId == injury.PlayerId); s.Injuries.Add(injury);
                }
                if (parsed.Injuries.Count > 0) s.Espn.InjuriesUpdatedAt = clock.GetUtcNow();
                s.Espn.LastSuccess = clock.GetUtcNow(); s.Espn.Issues = parsed.Issues;
                s.Espn.Status = $"ESPN {season}, week {week}: {parsed.Sets[0].Players.Count} matched projections; {parsed.Injuries.Count} player statuses checked. " + (parsed.Sets[0].Players.Count == 0 ? "No usable projections; saved estimates retained. " : "Create a new competition to use updated estimates. ") + "Injury badges update in existing drafts.";
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException or OperationCanceledException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            await store.Mutate(s => s.Espn.Status = "ESPN refresh failed. " + (ex is HttpRequestException ? "ESPN could not return projections; saved data retained." : ex is OperationCanceledException ? "Request interrupted; saved data retained." : ex.Message));
        }
        finally { gate.Release(); }
    }

    public sealed record Parsed(List<ProjectionSet> Sets, List<string> Issues, List<PlayerInjury> Injuries);
    public static Parsed Parse(string json, int season, int week, List<Player> players)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("players", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() is < 1 or > 2000) throw new InvalidDataException("ESPN returned an unexpected player pool.");
        if (doc.RootElement.TryGetProperty("seasonId", out var returnedSeason) && returnedSeason.GetInt32() != season) throw new InvalidDataException("ESPN returned a different season.");
        if (doc.RootElement.TryGetProperty("total", out var total) && total.GetInt32() > entries.GetArrayLength()) throw new InvalidDataException("ESPN truncated the player pool. Saved projections retained.");
        var lines = new List<(string Name, string Team, string Position, StatLine Line)>(); var issues = new List<string>(); var injuries = new List<PlayerInjury>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("player", out var p)) continue;
            var position = p.GetProperty("defaultPositionId").GetInt32() switch { 1 => "QB", 2 => "RB", 3 => "WR", 4 => "TE", 5 => "K", 16 => "D/ST", _ => "" };
            if (position.Length == 0 || position == "D/ST") continue;
            var name = p.GetProperty("fullName").GetString() ?? "";
            if (!Teams.TryGetValue(p.GetProperty("proTeamId").GetInt32(), out var team)) continue;
            var matchedPlayer = ProjectionImport.FindPlayer(players, name, team, position);
            var status = p.TryGetProperty("injuryStatus", out var injuryStatus) && injuryStatus.ValueKind == JsonValueKind.String ? injuryStatus.GetString() : null;
            var hurt = p.TryGetProperty("injured", out var injured) && injured.ValueKind == JsonValueKind.True;
            var designation = Designation(status, hurt);
            if (matchedPlayer is not null && designation is not null)
                injuries.Add(new() { PlayerId = matchedPlayer.Id, Designation = designation });
            if (!p.TryGetProperty("stats", out var stats)) continue;
            var matching = stats.EnumerateArray().Where(s => Int(s, "seasonId") == season && Int(s, "scoringPeriodId") == week && Int(s, "statSourceId") == 1 && Int(s, "statSplitTypeId") == 1).ToList();
            if (matching.Count != 1 || !matching[0].TryGetProperty("stats", out var values) || values.ValueKind != JsonValueKind.Object || !values.EnumerateObject().Any()) continue;
            double N(string key) => values.TryGetProperty(key, out var value) && value.TryGetDouble(out var n) && double.IsFinite(n) && n <= 2000 && n >= (key is "3" or "24" or "42" ? -100 : 0) ? n : values.TryGetProperty(key, out _) ? throw new InvalidDataException($"ESPN returned invalid projected stats for {name}.") : 0;
            bool Has(params string[] keys) => keys.All(k => values.TryGetProperty(k, out _));
            var core = position switch { "QB" => Has("3", "4", "20"), "K" => Has("74", "77", "80", "83", "86"), "RB" => Has("24", "25", "53", "42", "43"), _ => Has("53", "42", "43") };
            if (!core) { issues.Add($"{name}: incomplete projected stat categories; local estimate retained."); continue; }
            try
            {
            var line = new StatLine { PassYards = N("3"), PassTd = N("4"), Interceptions = N("20"), RushYards = N("24"), RushTd = N("25"), Receptions = N("53"), RecYards = N("42"), RecTd = N("43"), FumblesLost = N("72"), TwoPoint = N("19") + N("26") + N("44"), ReturnTd = N("101") + N("102"), FgShort = N("80"), Fg40 = N("77"), Fg50 = N("74"), ExtraPoints = N("86") };
            // Some schemas separate 60+ makes, others include them in 50+. Reconcile against total FG.
            if (position == "K" && Math.Abs(line.FgShort + line.Fg40 + line.Fg50 - N("83")) > .01)
            {
                line.Fg50 += N("201");
                if (Math.Abs(line.FgShort + line.Fg40 + line.Fg50 - N("83")) > .01) { issues.Add($"{name}: projected field goal distances do not reconcile; local estimate retained."); continue; }
            }
            lines.Add((name, team, position, line));
            }
            catch (InvalidDataException) { issues.Add($"{name}: invalid or negative projected count; local estimate retained."); }
        }
        issues.Add("D/ST uses local estimates: ESPN's projected points-allowed buckets do not match our scoring boundaries. Omitted optional offensive stat categories contribute zero to this estimate.");
        var sets = new List<ProjectionSet>();
        foreach (var ppr in new[] { 0d, .5, 1 })
        {
            var rows = lines.Select(x => new Dictionary<string, string> { ["Name"] = x.Name, ["Team"] = x.Team, ["Pos"] = x.Position, ["Points"] = x.Line.Points(ppr).ToString(CultureInfo.InvariantCulture) }).ToList();
            if (rows.Count == 0) { sets.Add(new() { Season = season, Week = week, Ppr = ppr, Source = Source }); continue; }
            var preview = ProjectionImport.Build(rows, players, "Name", "Team", "Pos", "Points"); issues.AddRange(preview.Issues);
            sets.Add(new() { Season = season, Week = week, Ppr = ppr, Source = Source, Players = preview.Matches.Select(m => new PlayerProjection { PlayerId = m.PlayerId, Points = m.Points }).ToList() });
        }
        return new(sets, issues.Distinct().ToList(), injuries.GroupBy(i => i.PlayerId).Where(g => g.Count() == 1).Select(g => g.Single()).ToList());
    }
    public static string? Designation(string? status, bool injured) => status?.ToUpperInvariant() switch
    {
        "ACTIVE" or "HEALTHY" or "NORMAL" => "",
        "QUESTIONABLE" => "Questionable", "DOUBTFUL" => "Doubtful", "OUT" => "Out",
        "INJURY_RESERVE" or "INJURED_RESERVE" or "IR" => "IR", "DAY_TO_DAY" => "Day-to-day",
        "PHYSICALLY_UNABLE_TO_PERFORM" or "PUP" => "PUP",
        _ => injured ? "Injured" : null
    };
    static int Int(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : -1;
    static readonly Dictionary<int, string> Teams = new() { [1]="ATL",[2]="BUF",[3]="CHI",[4]="CIN",[5]="CLE",[6]="DAL",[7]="DEN",[8]="DET",[9]="GB",[10]="TEN",[11]="IND",[12]="KC",[13]="LV",[14]="LAR",[15]="MIA",[16]="MIN",[17]="NE",[18]="NO",[19]="NYG",[20]="NYJ",[21]="PHI",[22]="ARI",[23]="PIT",[24]="LAC",[25]="SF",[26]="SEA",[27]="TB",[28]="WAS",[29]="CAR",[30]="JAX",[33]="BAL",[34]="HOU" };
}

public sealed class EspnProjectionWorker(EspnProjections projections, GameService store, TimeProvider clock, ILogger<EspnProjectionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try
            {
                var s = store.Snapshot(); var settings = s.Espn;
                if (settings.LeagueId > 0 && s.Games.Any(g => g.Season == settings.Season && g.Week == settings.Week && g.Kickoff > clock.GetUtcNow()))
                    await projections.Refresh(settings.LeagueId, settings.Season, settings.Week, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning(ex, "ESPN projection refresh failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
