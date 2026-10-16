using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SundayRivalry.Domain;
using SundayRivalry.Services;

static class GameChecks
{
public static async Task Run()
{
var checks = 0;
void Check(bool pass, string label) { if (!pass) throw new Exception("FAILED: " + label); Console.WriteLine("PASS " + label); checks++; }
async Task Reject(Func<Task> action, string label) { try { await action(); } catch (InvalidOperationException) { Check(true, label); return; } throw new Exception("Expected rejection: " + label); }
var temp = Path.Combine(Path.GetTempPath(), "SundayRivalryChecks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
var factory = new TestFactory(Path.Combine(temp, "checks.db"));
var clock = new TestClock();
var service = new GameService(factory, clock);
await service.Initialize();
Competition Current(string id) => GameService.Find(service.Snapshot(), id);
async Task<string> New(string format = "Snake") => await service.Create(2026, 1, format, 1, 200, "Verification", [], true);
async Task Start(string id) { await service.Ready(id, 0); await service.Ready(id, 1); }
try
{
    Check(Enumerable.Range(0, 8).Select(i => Rules.SnakeOwner(i, 0)).SequenceEqual(new[] { 0, 1, 1, 0, 0, 1, 1, 0 }), "Snake reverses order each round");
    Check(Rules.Fits("FLEX", "RB") && Rules.Fits("FLEX", "WR") && Rules.Fits("FLEX", "TE") && !Rules.Fits("FLEX", "QB"), "FLEX eligibility");
    var line = new StatLine { PassYards = 250, PassTd = 2, Interceptions = 1, RushYards = 20, Receptions = 5, RecYards = 60, RecTd = 1, FumblesLost = 1 };
    Check(Math.Abs(line.Points(1) - 33) < .001 && Math.Abs(line.Points(.5) - 30.5) < .001 && Math.Abs(line.Points(0) - 28) < .001, "Full PPR, half PPR, and standard scoring");
    Check(new StatLine { ExtraPoints = 3, FgShort = 1, Fg40 = 1, Fg50 = 1 }.Points(1) == 15, "Kicker distance scoring");
    Check(new StatLine { Defense = true, Sacks = 3, Takeaways = 2, DefenseTd = 1, PointsAllowed = 10 }.Points(1) == 17, "Defense scoring and points allowed");
    Check(new[] { 0, 6, 13, 20, 27, 34, 35 }.Select(StatLine.DefensePoints).SequenceEqual(new[] { 10, 7, 4, 1, 0, -1, -4 }), "Defense points-allowed boundaries");
    var sampleGame = new NflGame { Id = "completed", Season = 2026, Week = 1, Home = "KC", Away = "BUF", HomeScore = 14, AwayScore = 7, Kickoff = DateTimeOffset.UtcNow.AddDays(-4) };
    var resultState = new AppState { Games = [sampleGame] };
    var resultCompetition = new Competition { Season = 2026, Week = 1, Games = [sampleGame], Pool = [new() { Player = new() { Id = "backup", Position = "QB", Team = "KC" }, GameId = sampleGame.Id }], Picks = [new() { PlayerId = "backup", Owner = 0, Slot = "QB" }] };
    Check(GameService.Actual(resultState, resultCompetition, "backup") is null && !GameService.Final(resultState, resultCompetition), "Completed schedule alone does not finalize missing player data");
    foreach (var team in new[] { "KC", "BUF" }) { resultState.Stats.Add(new() { PlayerId = "starter-" + team, Team = team, Season = 2026, Week = 1 }); resultState.Stats.Add(new() { PlayerId = "DST-" + team, Team = team, Defense = true, Season = 2026, Week = 1 }); }
    Check(GameService.Actual(resultState, resultCompetition, "backup") is { DidNotRecordStats: true } && GameService.Final(resultState, resultCompetition), "Missing performance settles at zero only after completed feed coverage and publication buffer");
    var id = await New(); var c = Current(id); var candidate = c.Pool.First(p => p.Player.Position == "QB");
    await Reject(() => service.Pick(id, 0, candidate.Player.Id, "QB", c.Revision), "Draft waits for both participants");
    await Start(id); c = Current(id);
    await Reject(() => service.Pick(id, 1, candidate.Player.Id, "QB", c.Revision), "Reject out-of-turn selection");
    await service.Pick(id, 0, candidate.Player.Id, "QB", c.Revision);
    c = Current(id);
    await Reject(() => service.Pick(id, 1, candidate.Player.Id, "QB", c.Revision), "Snake players are exclusive");
    await service.Undo(id, 0, c.Revision); c = Current(id);
    Check(c.Picks.Count == 1 && c.UndoRequestedBy == 0, "Undo request preserves pick until rival approves");
    await service.Undo(id, 1, c.Revision); c = Current(id);
    Check(c.Picks.Count == 0 && c.UndoRequestedBy is null, "Mutual undo restores previous turn");
    var qb = c.Pool.First(p => p.Player.Position == "QB");
    var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ => { try { await service.Pick(id, 0, qb.Player.Id, "QB", c.Revision); return true; } catch (InvalidOperationException) { return false; } }));
    Check(outcomes.Count(x => x) == 1 && Current(id).Picks.Count == 1, "Concurrent selections commit exactly once");
    while (!Current(id).DraftComplete)
    {
        c = Current(id); var owner = c.Turn;
        var choice = c.Pool.First(p => Rules.OpenSlot(c, owner, p.Player.Position) is not null && !c.Picks.Any(x => x.PlayerId == p.Player.Id));
        await service.Pick(id, owner, choice.Player.Id, Rules.OpenSlot(c, owner, choice.Player.Position)!, c.Revision);
    }
    c = Current(id);
    Check(c.Picks.Count == 18 && c.Picks.Select(p => p.PlayerId).Distinct().Count() == 18, "Complete 18-pick exclusive snake draft");
    Check(Enumerable.Range(0, 2).All(o => c.Picks.Where(p => p.Owner == o).Select(p => p.Slot).Order().SequenceEqual(Rules.Slots.Order())), "Both snake rosters fill all nine slots");
    var salaryId = await New("Salary"); await Start(salaryId); c = Current(salaryId);
    await service.Mutate(s => GameService.Find(s, salaryId).Pool.First(p => p.Player.Position == "QB").Salary = 50);
    c = Current(salaryId);
    var shared = c.Pool.First(p => p.Player.Position == "QB");
    var independentRevision = c.Revision;
    await service.Pick(salaryId, 0, shared.Player.Id, "QB", c.Revision); c = Current(salaryId);
    Check(GameService.CreditsLeft(c, 0) == 150 && GameService.OpenPositions(c, 0) == 8 && GameService.AverageCreditsLeft(c, 0) == 18.75, "50-credit QB leaves 150 credits and an 18.75 average across eight open positions");
    Check(!GameService.CanAfford(c, 0, new Candidate { Salary = 151 }) && GameService.CanAfford(c, 0, new Candidate { Salary = 150 }), "Salary affordability uses current credits left and accepts an exact-budget price");
    await service.Pick(salaryId, 1, shared.Player.Id, "QB", independentRevision);
    Check(Current(salaryId).Picks.Count(p => p.PlayerId == shared.Player.Id) == 2, "Salary teams can share players");
    Check(Current(salaryId).Picks.Count == 2, "Salary picks do not require alternating turns or fail when the opponent saves first");
    for (int owner = 0; owner < 2; owner++)
    {
        while (Current(salaryId).Picks.Count(p => p.Owner == owner) < 9)
        {
            c = Current(salaryId);
            var choice = c.Pool.OrderBy(p => p.Salary).First(p => Rules.OpenSlot(c, owner, p.Player.Position) is not null && !c.Picks.Any(x => x.Owner == owner && x.PlayerId == p.Player.Id));
            await service.Pick(salaryId, owner, choice.Player.Id, Rules.OpenSlot(c, owner, choice.Player.Position)!, c.Revision);
        }
    }
    c = Current(salaryId);
    Check(c.DraftComplete && Enumerable.Range(0, 2).All(o => GameService.Spent(c, o) <= c.Budget), "Complete salary lineups stay within cap");
    Check(GameService.AverageCreditsLeft(c, 0) is null, "Full salary roster has no remaining-position average or divide-by-zero");
    var removed = c.Picks.First(p => p.Owner == 0); await service.Remove(salaryId, 0, removed.PlayerId, c.Revision);
    Check(Current(salaryId).Picks.Count == 17, "Salary lineup can be edited before kickoff");
    Check(GameService.OpenPositions(Current(salaryId), 0) == 1 && GameService.AverageCreditsLeft(Current(salaryId), 0) == GameService.CreditsLeft(Current(salaryId), 0), "Removing a player restores its credits and recalculates the open-position average");
    var budgetId = await New("Salary"); await Start(budgetId);
    await service.Mutate(s => GameService.Find(s, budgetId).Pool.First(p => p.Player.Position == "QB").Salary = 200);
    c = Current(budgetId); var costly = c.Pool.First(p => p.Player.Position == "QB");
    await Reject(() => service.Pick(budgetId, 0, costly.Player.Id, "QB", c.Revision), "Reserve budget for remaining positions");
    Check(Current(budgetId).Picks.Count == 0, "Rejected purchase rolls back state");
    var restart = new GameService(factory, clock); await restart.Initialize();
    Check(GameService.Find(restart.Snapshot(), id).Picks.Count == 18, "SQLite preserves completed draft after restart");
    clock.Now = Current(salaryId).LockAt;
    await Reject(() => service.Remove(salaryId, 0, Current(salaryId).Picks[0].PlayerId, Current(salaryId).Revision), "Salary edits lock exactly at kickoff");
    await Reject(() => service.Undo(id, 0, Current(id).Revision), "Snake undo locks at kickoff");
    var lockedRestart = new GameService(factory, clock); await lockedRestart.Initialize();
    await Reject(() => lockedRestart.Undo(id, 0, GameService.Find(lockedRestart.Snapshot(), id).Revision), "Kickoff lock survives application restart");
    clock.Now = DateTimeOffset.UtcNow;
    var fixture = "a,b,c\n\"Smith, Alex\",\"say \"\"hello\"\"\",3\n";
    var row = DataSync.Csv(fixture)[0];
    Check(row["a"] == "Smith, Alex" && row["b"] == "say \"hello\"", "CSV supports quoted commas and escaped quotes");
    // Actual public feed fixtures are optional for repeatability offline.
    var research = Path.GetFullPath(".tools/research");
    if (Environment.GetEnvironmentVariable("SUNDAY_RIVALRY_REAL_FIXTURES") == "1" && File.Exists(Path.Combine(research, "games.csv")))
    {
        var sync = new DataSync(service, new UnusedHttpFactory(), NullLogger<DataSync>.Instance);
        await sync.ImportDirectory(research); var first = service.Snapshot();
        await sync.ImportDirectory(research); var second = service.Snapshot();
        Check(first.Stats.Count == second.Stats.Count && first.Players.Count == second.Players.Count && first.Games.Count == second.Games.Count, "Real feed imports are idempotent");
        Check(second.Players.Count > 200 && second.Stats.Count > 100 && second.Games.Any(g => g.Season == 2026), "Real NFL schedules, players, and statistics imported");
        Check(second.Players.Any(p => p.Position == "K") && second.Stats.Any(s => s.Defense), "Real kicker and D/ST data available");
        var weeks = second.Games.Where(g => g.Season == 2026 && g.Kickoff > clock.Now && g.Day == "Sunday").GroupBy(g => g.Week).OrderBy(g => g.Key);
        var slate = weeks.First().ToList();
        var liveId = await service.Create(2026, slate[0].Week, "Snake", 1, 200, "Real NFL slate verification", slate.Select(g => g.Id).ToList());
        var live = Current(liveId);
        Check(live.Pool.All(p => live.Games.Any(g => g.Id == p.GameId)), "Real competition pool contains only included games");
        var originalEstimate = live.Pool[0].Projection;
        await service.Mutate(s => s.Stats.Add(new() { PlayerId = live.Pool[0].Player.Id, Season = live.Season, Week = live.Week, Receptions = 999 }));
        Check(Current(liveId).Pool[0].Projection == originalEstimate, "Competition estimates remain frozen after sync");
        var freshPool = GameService.BuildPool(second.Players, service.Snapshot().Stats, live);
        Check(freshPool.First(p => p.Player.Id == live.Pool[0].Player.Id).Projection == originalEstimate, "Estimates exclude current-week outcomes");
        await service.Mutate(s => s.Stats.First(x => x.PlayerId == live.Pool[0].Player.Id && x.Season == live.Season && x.Week == live.Week).Receptions = 2);
        Check(GameService.Actual(service.Snapshot(), live, live.Pool[0].Player.Id)!.Receptions == 2, "Stat corrections replace scores without altering draft snapshots");
    }
    await DraftPrivacyChecks.Run(service);
    await ProjectionChecks.Run();
    Console.WriteLine($"\n{checks} game checks passed. Temporary database: {temp}");
}
finally { SqliteCleanup(); }
void SqliteCleanup() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }


}
}
