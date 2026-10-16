using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SundayRivalry.Domain;

namespace SundayRivalry.Services;

// A single writer serializes the two-person game's mutations. SQLite is durable;
// a failed write never publishes a half-applied in-memory draft.
public class GameService(IDbContextFactory<StateDb> factory, TimeProvider clock)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private AppState state = new();
    public event Action? Changed;
    public AppState Snapshot() => WithClock(Clone(state));
    private AppState WithClock(AppState value)
    {
        foreach (var competition in value.Competitions) competition.Clock = clock;
        return value;
    }
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    public async Task Initialize()
    {
        using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var row = await db.State.FindAsync(1);
        state = WithClock(row is null ? new() : JsonSerializer.Deserialize<AppState>(row.Json)!);
    }

    public async Task Mutate(Action<AppState> change)
    {
        await gate.WaitAsync();
        try
        {
            var next = WithClock(Clone(state));
            change(next);
            WithClock(next);
            using var db = factory.CreateDbContext();
            var row = await db.State.FindAsync(1);
            if (row is null) { row = new() { Id = 1 }; db.State.Add(row); }
            row.Json = JsonSerializer.Serialize(next);
            await db.SaveChangesAsync();
            state = next;
        }
        finally { gate.Release(); }
        Changed?.Invoke();
    }

    public async Task<string> Create(int season, int week, string format, double ppr, int budget, string title, List<string> gameIds, bool demo = false)
    {
        var id = "";
        await Mutate(s =>
        {
            if (format is not ("Snake" or "Salary") || ppr is not (0 or .5 or 1) || budget < 50 || budget > 1000)
                throw new InvalidOperationException("Choose a valid format, scoring preset, and budget between 50 and 1,000.");
            var games = demo ? DemoData.Games(clock.GetUtcNow()) : s.Games.Where(g => gameIds.Contains(g.Id) && g.Season == season && g.Week == week).ToList();
            if (games.Count == 0 || games.Any(g => g.Kickoff <= clock.GetUtcNow()))
                throw new InvalidOperationException("Choose at least one game. Every included game must still be upcoming.");
            var c = new Competition
            {
                Title = string.IsNullOrWhiteSpace(title) ? $"Week {week} showdown" : title.Trim()[..Math.Min(title.Trim().Length, 80)],
                Season = season, Week = week, Format = format, Ppr = ppr, Budget = budget, CreatedAt = clock.GetUtcNow(), Clock = clock,
                Games = Clone(games), Names = [.. s.Names], Demo = demo,
                FirstOwner = s.Competitions.Count(x => !x.Demo && x.Format == "Snake") % 2
            };
            c.Pool = BuildPool(demo ? DemoData.Players() : s.Players, s.Stats, c, demo ? null : s.Projections);
            if (!CanComplete(c, 0) || !CanComplete(c, 1))
                throw new InvalidOperationException("This slate doesn't have enough eligible players for two complete rosters. Include more games or sync roster data.");
            if (format == "Snake" && !CanDraftBoth(c))
                throw new InvalidOperationException("This slate is too small for two exclusive rosters. Include more games or choose salary cap.");
            s.Competitions.Add(c); id = c.Id;
        });
        return id;
    }

    public static List<Candidate> BuildPool(List<Player> players, List<StatLine> stats, Competition c, List<ProjectionSet>? projections = null)
    {
        var pool = new List<Candidate>();
        var imports = projections?.Where(p => p.Season == c.Season && p.Week == c.Week && p.Ppr == c.Ppr)
            .OrderByDescending(p => p.Source == EspnProjections.Source).ThenByDescending(p => p.ImportedAt).ToList() ?? [];
        foreach (var player in players)
        {
            var game = c.Games.FirstOrDefault(g => g.Home == player.Team || g.Away == player.Team);
            if (game is null || !Rules.Positions.Contains(player.Position)) continue;
            // Only prior games: never leak the competition's outcome into its estimate.
            var recent = stats.Where(x => x.PlayerId == player.Id && (x.Season < c.Season || (x.Season == c.Season && x.Week < c.Week)))
                .OrderByDescending(x => x.Season).ThenByDescending(x => x.Week).Take(5).ToList();
            double average = recent.Count == 0 ? player.Position switch { "QB" => 13, "RB" => 5, "WR" => 5, "TE" => 4, "K" => 6, _ => 6 }
                : recent.Average(x => x.Points(c.Ppr));
            if (c.Demo) average += (player.Name.Sum(x => (int)x) % 120) / 10.0;
            var imported = imports.FirstOrDefault(set => set.Players.Any(p => p.PlayerId == player.Id));
            var supplied = imported?.Players.FirstOrDefault(p => p.PlayerId == player.Id);
            var estimate = Math.Round(supplied?.Points ?? Math.Max(0, average), 1);
            pool.Add(new() { Player = Clone(player), GameId = game.Id, Opponent = game.Home == player.Team ? $"vs {game.Away}" : $"@ {game.Home}",
                Kickoff = game.Kickoff, Projection = estimate, RecentAverage = Math.Round(average, 1), SampleSize = recent.Count,
                Salary = Math.Max(1, (int)Math.Round((3 + estimate * 1.1) * c.Budget / 200.0)),
                ProjectionSource = supplied is null ? "" : imported!.Source, ProjectionImportedAt = supplied?.ImportedAt });
        }
        return pool.OrderByDescending(x => x.Projection).ThenBy(x => x.Player.Name).ToList();
    }

    public async Task Ready(string id, int owner) => await Mutate(s =>
    {
        CheckOwner(owner); var c = Find(s, id); EnsureOpen(c);
        if (c.Started) return;
        c.Ready[owner] = !c.Ready[owner]; c.Revision++;
    });

    public async Task Pick(string id, int owner, string playerId, string slot, int revision) => await Mutate(s =>
    {
        CheckOwner(owner); var c = Find(s, id); EnsureOpen(c);
        // Salary selections are independent. Validate against the latest roster
        // and budget without rejecting a pick because the opponent just edited.
        if (c.Format == "Snake") CheckRevision(c, revision);
        if (!c.Started) throw new InvalidOperationException("Both players need to be ready first.");
        if (c.Format == "Snake" && c.Turn != owner) throw new InvalidOperationException("It's your rival's turn.");
        var p = c.Pool.FirstOrDefault(x => x.Player.Id == playerId) ?? throw new InvalidOperationException("Player is not in this slate.");
        if (!Rules.Slots.Contains(slot) || !Rules.Fits(slot, p.Player.Position) || c.Picks.Any(x => x.Owner == owner && (x.Slot == slot || x.PlayerId == playerId)))
            throw new InvalidOperationException("Choose an open, eligible roster slot.");
        if (c.Format == "Snake" && c.Picks.Any(x => x.PlayerId == playerId)) throw new InvalidOperationException("That player has already been drafted.");
        c.Picks.Add(new() { Owner = owner, PlayerId = playerId, Slot = slot, At = clock.GetUtcNow() });
        if (c.Format == "Salary" && !CanComplete(c, owner)) throw new InvalidOperationException("Save enough credits to fill every remaining roster slot. Try a less expensive player.");
        if (c.Format == "Snake" && !CanDraftBoth(c)) throw new InvalidOperationException("That selection would leave an unfillable roster. Choose a different player or slot.");
        c.UndoRequestedBy = null; c.Revision++;
    });

    public async Task Remove(string id, int owner, string playerId, int revision) => await Mutate(s =>
    {
        CheckOwner(owner); var c = Find(s, id); EnsureOpen(c);
        if (c.Format != "Salary") throw new InvalidOperationException("Snake picks require a mutual undo.");
        c.Picks.RemoveAll(x => x.Owner == owner && x.PlayerId == playerId); c.Revision++;
    });

    public async Task Undo(string id, int owner, int revision) => await Mutate(s =>
    {
        CheckOwner(owner); var c = Find(s, id); EnsureOpen(c); CheckRevision(c, revision);
        if (c.Format != "Snake" || c.Picks.Count == 0) throw new InvalidOperationException("There is no snake pick to undo.");
        if (c.UndoRequestedBy is null) c.UndoRequestedBy = owner;
        else if (c.UndoRequestedBy == owner) c.UndoRequestedBy = null;
        else { c.Picks.RemoveAt(c.Picks.Count - 1); c.UndoRequestedBy = null; }
        c.Revision++;
    });

    public static int Spent(Competition c, int owner) => c.Picks.Where(p => p.Owner == owner).Sum(p => c.Pool.First(x => x.Player.Id == p.PlayerId).Salary);
    public static int CreditsLeft(Competition c, int owner) => c.Budget - Spent(c, owner);
    public static int OpenPositions(Competition c, int owner) => Rules.Slots.Count(slot => !c.Picks.Any(p => p.Owner == owner && p.Slot == slot));
    public static double? AverageCreditsLeft(Competition c, int owner)
    {
        var open = OpenPositions(c, owner);
        return open > 0 ? (double)CreditsLeft(c, owner) / open : null;
    }
    public static bool CanAfford(Competition c, int owner, Candidate player) => player.Salary <= CreditsLeft(c, owner);

    // Position slots are disjoint apart from FLEX. Enumerating FLEX's three
    // positions gives an exact inexpensive feasibility / minimum-cost check.
    public static bool CanComplete(Competition c, int owner)
    {
        var open = Rules.Slots.Where(s => !c.Picks.Any(p => p.Owner == owner && p.Slot == s)).ToList();
        var available = c.Pool.Where(p => !c.Picks.Any(x => x.PlayerId == p.Player.Id && (x.Owner == owner || c.Format == "Snake"))).ToList();
        foreach (var flex in new[] { "RB", "WR", "TE" })
        {
            var needed = open.GroupBy(s => s == "FLEX" ? flex : s.StartsWith("RB") ? "RB" : s.StartsWith("WR") ? "WR" : s).ToDictionary(g => g.Key, g => g.Count());
            var cost = 0; var fits = true;
            foreach (var (pos, count) in needed)
            {
                var choices = available.Where(p => p.Player.Position == pos).OrderBy(p => p.Salary).Take(count).ToList();
                if (choices.Count != count) { fits = false; break; }
                cost += choices.Sum(p => p.Salary);
            }
            if (fits && (c.Format != "Salary" || cost + Spent(c, owner) <= c.Budget)) return true;
        }
        return false;
    }

    public static bool CanDraftBoth(Competition c)
    {
        var open = Enumerable.Range(0, 2).Select(o => Rules.Slots.Where(s => !c.Picks.Any(p => p.Owner == o && p.Slot == s)).ToList()).ToArray();
        var available = c.Pool.Where(p => !c.Picks.Any(x => x.PlayerId == p.Player.Id)).GroupBy(p => p.Player.Position).ToDictionary(g => g.Key, g => g.Count());
        foreach (var f0 in new[] { "RB", "WR", "TE" })
        foreach (var f1 in new[] { "RB", "WR", "TE" })
        {
            var needs = open[0].Select(s => Position(s, f0)).Concat(open[1].Select(s => Position(s, f1))).GroupBy(s => s);
            if (needs.All(g => available.GetValueOrDefault(g.Key) >= g.Count())) return true;
        }
        return false;
    }
    private static string Position(string s, string flex) => s == "FLEX" ? flex : s.StartsWith("RB") ? "RB" : s.StartsWith("WR") ? "WR" : s;
    public static Competition Find(AppState s, string id) => s.Competitions.FirstOrDefault(c => c.Id == id) ?? throw new InvalidOperationException("Competition not found.");
    private static void CheckOwner(int owner) { if (owner is < 0 or > 1) throw new InvalidOperationException("Unknown participant."); }
    private static void CheckRevision(Competition c, int revision) { if (c.Revision != revision) throw new InvalidOperationException("The room changed. Review the latest picks and try again."); }
    private void EnsureOpen(Competition c) { if (clock.GetUtcNow() >= c.LockAt) throw new InvalidOperationException("Rosters locked at the first kickoff."); }

    public static StatLine? Actual(AppState s, Competition c, string playerId)
    {
        if (c.Demo) return null;
        var recorded = s.Stats.FirstOrDefault(x => x.PlayerId == playerId && x.Season == c.Season && x.Week == c.Week);
        var candidate = c.Pool.FirstOrDefault(x => x.Player.Id == playerId);
        var live = s.LiveScores.FirstOrDefault(b => b.GameId == candidate?.GameId);
        var liveStat = live?.Stats.FirstOrDefault(line => line.PlayerId == playerId);
        // Historical publication may lag. Keep the live snapshot separate until
        // a later historical import is available after the correction buffer.
        if (liveStat is not null && (recorded is null || candidate is null || c.Clock.GetUtcNow() < candidate.Kickoff.AddDays(2) || s.LastSync <= live!.UpdatedAt || s.LastSync is null)) return liveStat;
        if (recorded is not null) return recorded;
        var game = s.Games.FirstOrDefault(g => g.Id == candidate?.GameId);
        // A missing row stays pending until both team and player feeds cover the
        // completed game, plus a two-day publication buffer. Then it is a zero
        // stat performance, rather than an indefinitely pending backup or DNP.
        if (candidate is null || candidate.Player.Position == "D/ST" || game is null || game.HomeScore is null || game.AwayScore is null
            || c.Clock.GetUtcNow() < game.Kickoff.AddDays(2)) return null;
        var weekStats = s.Stats.Where(x => x.Season == c.Season && x.Week == c.Week).ToList();
        if (new[] { game.Home, game.Away }.All(team => weekStats.Any(x => x.Team == team && x.Defense)
            && weekStats.Any(x => x.Team == team && !x.Defense)))
            return new() { PlayerId = playerId, Team = candidate.Player.Team, Season = c.Season, Week = c.Week, DidNotRecordStats = true };
        return null;
    }
    public static bool IsUnavailable(AppState state, Competition competition, string playerId) => !competition.Demo && state.Injuries.FirstOrDefault(i => i.PlayerId == playerId)?.Designation is "Out" or "IR";
    public static double EstimatedPoints(AppState state, Competition competition, Candidate candidate) => IsUnavailable(state, competition, candidate.Player.Id) ? 0 : candidate.Projection;
    public static string EstimateLabel(AppState state, Competition competition, Candidate candidate) => IsUnavailable(state, competition, candidate.Player.Id)
        ? $"{state.Injuries.First(i => i.PlayerId == candidate.Player.Id).Designation} · 0 projected" : candidate.EstimateLabel;
    public static double Total(AppState s, Competition c, int owner, bool projected) => Math.Round(c.Picks.Where(p => p.Owner == owner).Sum(p => projected ? EstimatedPoints(s, c, c.Pool.First(x => x.Player.Id == p.PlayerId)) : Actual(s, c, p.PlayerId)?.Points(c.Ppr) ?? 0), 2);
    public static bool Final(AppState s, Competition c) => !c.Demo && c.Games.All(g => s.Games.Any(x => x.Id == g.Id && x.HomeScore.HasValue && x.AwayScore.HasValue))
        && c.Picks.All(p => Actual(s, c, p.PlayerId) is { IsLive: false }) && c.Clock.GetUtcNow() > c.Games.Max(g => g.Kickoff).AddDays(2);
}

