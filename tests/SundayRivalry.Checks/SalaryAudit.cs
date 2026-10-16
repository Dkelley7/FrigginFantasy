using System.Text.Json;
using Microsoft.Data.Sqlite;
using SundayRivalry.Domain;
using SundayRivalry.Services;

// Read-only diagnostics: never initialize a service against the live database.
static class SalaryAudit
{
    public static async Task Run(string[] args)
    {
        var index = Array.IndexOf(args, "--salary-audit");
        if (index + 1 >= args.Length) throw new ArgumentException("Supply a database path after --salary-audit.");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(args[index + 1]), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = "SELECT Json FROM State WHERE Id=1";
        var state = JsonSerializer.Deserialize<AppState>((string)(await command.ExecuteScalarAsync())!)!;
        var results = new List<object>();
        foreach (var week in state.Games.Where(g => g.Kickoff > DateTimeOffset.UtcNow).OrderBy(g => g.Kickoff).GroupBy(g => (g.Season, g.Week)).Take(3))
        foreach (var ppr in new[] { 0d, .5, 1 })
        foreach (var slate in new[] { (Name: "All upcoming", Games: week.ToList()), (Name: "Sunday", Games: week.Where(g => g.Day == "Sunday").ToList()), (Name: "Single game", Games: week.Take(1).ToList()) })
        {
            if (slate.Games.Count == 0) continue;
            var c = new Competition { Season = week.Key.Season, Week = week.Key.Week, Format = "Salary", Ppr = ppr, Budget = 200, Games = slate.Games };
            c.Pool = GameService.BuildPool(state.Players, state.Stats, c, state.Projections);
            var best = Optimize(c.Pool, cheapest: false); var cheapest = Optimize(c.Pool, cheapest: true);
            if (best is null) { results.Add(new { c.Season, c.Week, ppr, slate = slate.Name, eligiblePlayers = c.Pool.Count, feasible = false }); continue; }
            results.Add(new { c.Season, c.Week, ppr, slate = slate.Name, games = slate.Games.Count, eligiblePlayers = c.Pool.Count, importedProjections = c.Pool.Count(p => p.ProjectionSource.Length > 0), baselineEstimates = c.Pool.Count(p => p.SampleSize == 0 && p.ProjectionSource.Length == 0), bestProjectedPoints = Math.Round(best.Value.Points, 2), bestLineupCost = best.Value.Cost, capIsBinding = best.Value.Cost > 200, cheapestLineupCost = cheapest!.Value.Cost, mostExpensivePlayer = c.Pool.Max(p => p.Salary) });
        }
        var report = new { auditedAt = DateTimeOffset.UtcNow, state.LastSync, players = state.Players.Count, stats = state.Stats.Count, projectionSets = state.Projections.Count, results };
        Directory.CreateDirectory("artifacts");
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync("artifacts/salary-audit.json", json); Console.WriteLine(json);
    }

    // A player can occupy only one slot; descending masks prevent player reuse.
    internal static (double Points, int Cost)? Optimize(List<Candidate> pool, bool cheapest)
    {
        var states = new (double Points, int Cost)?[1 << Rules.Slots.Length]; states[0] = (0, 0);
        foreach (var candidate in pool)
        for (var mask = states.Length - 1; mask >= 0; mask--)
        {
            if (states[mask] is not { } previous) continue;
            for (var slot = 0; slot < Rules.Slots.Length; slot++)
            {
                if ((mask & (1 << slot)) != 0 || !Rules.Fits(Rules.Slots[slot], candidate.Player.Position)) continue;
                var next = mask | (1 << slot); var points = previous.Points + candidate.Projection; var cost = previous.Cost + candidate.Salary;
                var current = states[next];
                if (current is null || (cheapest ? cost < current.Value.Cost : points > current.Value.Points + .000001 || Math.Abs(points - current.Value.Points) < .000001 && cost < current.Value.Cost)) states[next] = (points, cost);
            }
        }
        return states[^1];
    }
}
