using System.Globalization;
using System.Text.Json;
using SundayRivalry.Domain;

namespace SundayRivalry.Services;

// Tolerate numeric strings, but never present omitted scoring categories as
// confirmed zero. Raw provider responses are retained locally for validation.
public static class Tank01Parser
{
    static JsonElement Field(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return default;
        foreach (var name in names)
            foreach (var property in value.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }
    static string Text(JsonElement value, params string[] names) => Field(value, names).ToString();
    static IEnumerable<JsonElement> Items(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => value.EnumerateArray().ToArray(),
        JsonValueKind.Object => value.EnumerateObject().Select(p => p.Value).ToArray(), _ => []
    };
    public static string Team(string value) => value.ToUpperInvariant() switch { "WSH" => "WAS", "JAC" => "JAX", "LA" => "LAR", "ARZ" => "ARI", var team => team };
    static string Name(string value) => string.Concat(value.Normalize(System.Text.NormalizationForm.FormD).Where(char.IsLetterOrDigit)).ToUpperInvariant();
    static double? Number(JsonElement value, params string[] names)
    {
        var raw = Field(value, names);
        return double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    }
    static JsonElement Body(JsonElement root)
    {
        var code = Number(root, "statusCode");
        if (code.HasValue && code != 200) throw new InvalidDataException("Tank01 returned an unsuccessful payload.");
        var body = Field(root, "body");
        if (body.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) throw new InvalidDataException("Tank01 payload has no usable body.");
        return body;
    }
    public static Dictionary<string, string> Schedule(string json, IEnumerable<NflGame> games)
    {
        using var document = JsonDocument.Parse(json); var body = Body(document.RootElement); var found = new Dictionary<string, string>();
        foreach (var item in Items(body))
        {
            var providerId = Text(item, "gameID");
            if (!System.Text.RegularExpressions.Regex.IsMatch(providerId, @"^\d{8}_[A-Z]{2,3}@[A-Z]{2,3}$")) continue;
            var away = Team(Text(item, "away")); var home = Team(Text(item, "home"));
            foreach (var game in games.Where(g => g.Away == away && g.Home == home
                && TimeZoneInfo.ConvertTime(g.Kickoff, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).ToString("yyyyMMdd") == providerId[..8])) found[game.Id] = providerId;
        }
        return found;
    }
    public static LiveBoxScore BoxScore(string json, NflGame game, string providerId, List<Player> players, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json); var body = Body(document.RootElement);
        if (Team(Text(body, "away")) != game.Away || Team(Text(body, "home")) != game.Home)
            throw new InvalidDataException("Box-score teams do not match the selected game.");
        var returnedId = Text(body, "gameID");
        if (returnedId.Length > 0 && returnedId != providerId) throw new InvalidDataException("Box-score game ID does not match.");
        var status = Text(body, "gameStatus");
        var result = new LiveBoxScore { GameId = game.Id, ProviderGameId = providerId, UpdatedAt = now, Final = status.Equals("Completed", StringComparison.OrdinalIgnoreCase) || status.Equals("Final", StringComparison.OrdinalIgnoreCase) };
        double Read(StatLine line, JsonElement group, string label, params string[] keys)
        {
            var value = Number(group, keys);
            if (!value.HasValue) line.CoverageIssues.Add(label + " not reported");
            return value ?? 0;
        }
        foreach (var raw in Items(Field(body, "playerStats")))
        {
            var team = Team(Text(raw, "teamAbv", "team")); var name = Text(raw, "longName", "playerName");
            var matches = players.Where(p => p.Position != "D/ST" && p.Team == team && Name(p.Name) == Name(name)).ToList();
            if (matches.Count != 1) { if (name.Length > 0) result.Issues.Add("Unmatched player: " + name); continue; }
            var player = matches[0]; var line = new StatLine { PlayerId = player.Id, Team = team, Season = game.Season, Week = game.Week, IsLive = true, UpdatedAt = now };
            var passing = Field(raw, "Passing"); var rushing = Field(raw, "Rushing"); var receiving = Field(raw, "Receiving");
            if (passing.ValueKind == JsonValueKind.Object)
            {
                line.PassYards = Read(line, passing, "Passing yards", "passYds"); line.PassTd = Read(line, passing, "Passing touchdowns", "passTD"); line.Interceptions = Read(line, passing, "Passing interceptions", "int");
            }
            else line.CoverageIssues.Add("Passing category not reported");
            if (rushing.ValueKind == JsonValueKind.Object)
            {
                line.RushYards = Read(line, rushing, "Rushing yards", "rushYds"); line.RushTd = Read(line, rushing, "Rushing touchdowns", "rushTD");
            }
            else line.CoverageIssues.Add("Rushing category not reported");
            if (receiving.ValueKind == JsonValueKind.Object)
            {
                line.Receptions = Read(line, receiving, "Receptions", "receptions"); line.RecYards = Read(line, receiving, "Receiving yards", "recYds"); line.RecTd = Read(line, receiving, "Receiving touchdowns", "recTD");
            }
            else line.CoverageIssues.Add("Receiving category not reported");
            var fumbles = Field(raw, "Fumbles", "Defense"); line.FumblesLost = Read(line, fumbles, "Lost fumbles", "fumblesLost");
            line.TwoPoint = Read(line, passing, "Passing two-point conversions", "passingTwoPointConversion")
                + Read(line, rushing, "Rushing two-point conversions", "rushingTwoPointConversion")
                + Read(line, receiving, "Receiving two-point conversions", "receivingTwoPointConversion");
            var returns = Field(raw, "Returns", "Return");
            line.ReturnTd = Number(returns, "returnTD") ??
                Read(line, Field(raw, "Kicking"), "Kick return touchdowns", "kickReturnTD") + Read(line, Field(raw, "Punting"), "Punt return touchdowns", "puntReturnTD");
            if (player.Position == "K")
            {
                var kicking = Field(raw, "Kicking");
                line.ExtraPoints = Read(line, kicking, "Extra points", "xpMade", "extraPointsMade");
                // Total FG made alone cannot reproduce our distance-based scoring.
                line.FgShort = Read(line, kicking, "Field goals under 40 yards", "fgMade0_39", "fgMadeUnder40");
                line.Fg40 = Read(line, kicking, "Field goals 40–49 yards", "fgMade40_49");
                line.Fg50 = Read(line, kicking, "Field goals 50+ yards", "fgMade50Plus");
                if (line.CoverageIssues.Any(i => i.StartsWith("Field goals")))
                {
                    // The verified provider response includes made FG distances in
                    // player scoring plays. Use them only when they reconcile with
                    // the explicit made-FG counter; never infer from the longest FG.
                    var made = Number(kicking, "fgMade");
                    var fgPlays = Items(Field(raw, "scoringPlays")).Where(p => Text(p, "scoreType") == "FG")
                        .DistinctBy(p => Text(p, "scorePeriod") + "|" + Text(p, "scoreTime") + "|" + Text(p, "score") + "|" + Text(p, "homeScore") + "|" + Text(p, "awayScore")).ToList();
                    var distances = fgPlays.Select(p => System.Text.RegularExpressions.Regex.Match(Text(p, "score"), @"\b(\d{1,2})\s+Yd\s+Field Goal\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
                    if (made is >= 0 && made == fgPlays.Count && distances.All(m => m.Success && int.Parse(m.Groups[1].Value) is > 0 and <= 80))
                    {
                        var yards = distances.Select(m => int.Parse(m.Groups[1].Value)).ToList();
                        line.FgShort = yards.Count(y => y < 40); line.Fg40 = yards.Count(y => y is >= 40 and < 50); line.Fg50 = yards.Count(y => y >= 50);
                        line.CoverageIssues.RemoveAll(i => i.StartsWith("Field goals"));
                    }
                }
            }
            // Require at least a primary scoring category rather than generating
            // a live zero for metadata-only records.
            if (passing.ValueKind == JsonValueKind.Object || rushing.ValueKind == JsonValueKind.Object || receiving.ValueKind == JsonValueKind.Object || Field(raw, "Kicking").ValueKind == JsonValueKind.Object)
                result.Stats.Add(line);
        }
        var defense = Field(body, "DST");
        foreach (var side in new[] { "away", "home" })
        {
            var raw = Field(defense, side); if (raw.ValueKind != JsonValueKind.Object) continue;
            var team = Team(Text(raw, "teamAbv")); if (team != (side == "home" ? game.Home : game.Away)) continue;
            var line = new StatLine { PlayerId = "DST-" + team, Team = team, Season = game.Season, Week = game.Week, Defense = true, IsLive = true, UpdatedAt = now };
            line.Sacks = Read(line, raw, "Defensive sacks", "sacks");
            line.Takeaways = Read(line, raw, "Defensive interceptions", "defensiveInterceptions") + Read(line, raw, "Opponent fumbles recovered", "fumblesRecovered");
            line.DefenseTd = Read(line, raw, "Defensive touchdowns", "defTD");
            line.CoverageIssues.Add("Special-teams return touchdowns and event overlap need provider validation");
            line.Safeties = Read(line, raw, "Defensive safeties", "safeties"); line.Blocks = Read(line, raw, "Blocked kicks", "blockedKicks");
            var allowed = Number(body, side == "home" ? "awayPts" : "homePts");
            if (allowed is null || allowed < 0 || allowed > 200 || allowed != Math.Truncate(allowed.Value))
            { result.Issues.Add("No usable scoreboard total for " + team + " defense; defense score remains pending."); continue; }
            line.PointsAllowed = (int)allowed.Value; result.Stats.Add(line);
        }
        if (result.Stats.Count == 0) throw new InvalidDataException("No matched scoring statistics were available; saved scores were retained.");
        if (result.Stats.Any(s => s.CoverageIssues.Count > 0)) result.Issues.Add("Partial scoring coverage. Unreported categories are not confirmed zero; historical imports will reconcile later.");
        return result;
    }
}
