using System.Globalization;
using SundayRivalry.Domain;

namespace SundayRivalry.Services;

public static class ProjectionImport
{
    public sealed record Match(string Name, string Team, string Position, string PlayerId, double Points);
    public sealed record Preview(List<Match> Matches, List<string> Issues);
    private static string Key(string value) => string.Concat(value.Normalize(System.Text.NormalizationForm.FormD)
        .Where(char.IsLetterOrDigit)).ToUpperInvariant();
    private static string Team(string value) => Key(value) switch
    {
        "JAC" => "JAX", "WSH" => "WAS", "LA" => "LAR", "ARZ" => "ARI", "BLT" => "BAL", "CLV" => "CLE", "HST" => "HOU", var team => team
    };
    private static string Position(string value) => Key(value) switch { "DST" or "DEF" or "D" => "DST", var pos => pos };
    public static string Guess(IEnumerable<string> headers, params string[] aliases) =>
        headers.FirstOrDefault(h => aliases.Any(a => Key(h) == Key(a))) ?? "";
    public static Player? FindPlayer(List<Player> players, string name, string team, string position)
    {
        var candidates = players.Where(p => Team(p.Team) == Team(team) && Position(p.Position) == Position(position) && (Position(position) == "DST" || Key(p.Name) == Key(name))).Take(2).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    public static Preview Build(List<Dictionary<string, string>> rows, List<Player> players, string nameColumn, string teamColumn, string positionColumn, string pointsColumn)
    {
        if (rows.Count == 0 || rows.Count > 5000) throw new InvalidDataException("Choose a projection CSV with between 1 and 5,000 player rows.");
        var columns = new[] { nameColumn, teamColumn, positionColumn, pointsColumn };
        if (columns.Distinct().Count() != 4 || columns.Any(c => !rows[0].ContainsKey(c)))
            throw new InvalidDataException("Select four different columns for player name, team, position, and projected points.");
        var matches = new List<Match>(); var issues = new List<string>(); var seen = new HashSet<string>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var name = row[nameColumn].Trim(); var team = Team(row[teamColumn]); var position = Position(row[positionColumn]);
            if (!double.TryParse(row[pointsColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out var points) || !double.IsFinite(points) || points is < -100 or > 200)
            { issues.Add($"Row {i + 2}: {name} has no valid projected-points value. Use projections, not salary or historical averages."); continue; }
            var player = FindPlayer(players, name, team, position);
            if (player is null)
            { issues.Add($"Row {i + 2}: {name} ({row[teamColumn]}, {row[positionColumn]}) could not be matched uniquely. Sync NFL data or check the selected columns."); continue; }
            if (!seen.Add(player.Id)) throw new InvalidDataException($"The CSV contains more than one projection for {player.Name}. Export a single week and scoring format.");
            matches.Add(new(player.Name, player.Team, player.Position, player.Id, points));
        }
        return new(matches, issues);
    }

    public static Task Save(GameService store, Preview preview, int season, int week, double ppr) => store.Mutate(state =>
    {
        if (season is < 2020 or > 2100 || week is < 1 or > 18 || ppr is not (0 or .5 or 1) || preview.Matches.Count == 0)
            throw new InvalidDataException("Choose a valid season, NFL week, scoring format, and at least one matched projection.");
        if (preview.Matches.Any(m => !state.Players.Any(p => p.Id == m.PlayerId && p.Team == m.Team) || !double.IsFinite(m.Points) || m.Points is < -100 or > 200))
            throw new InvalidDataException("The player database changed. Preview the CSV again before saving.");
        // Preserve prior position exports when another position CSV is imported.
        var prior = state.Projections.FirstOrDefault(p => p.Season == season && p.Week == week && p.Ppr == ppr);
        var merged = preview.Matches.Select(m => new PlayerProjection { PlayerId = m.PlayerId, Points = m.Points, ImportedAt = DateTimeOffset.UtcNow })
            .Concat(prior?.Players ?? []).DistinctBy(p => p.PlayerId).ToList();
        state.Projections.RemoveAll(p => p.Season == season && p.Week == week && p.Ppr == ppr);
        state.Projections.Add(new() { Season = season, Week = week, Ppr = ppr, ImportedAt = DateTimeOffset.UtcNow, Players = merged });
    });
}
