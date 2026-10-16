using SundayRivalry.Domain;
using SundayRivalry.Services;
using System.Text.Json;

static class LifecycleChecks
{
    public static async Task Run(int seed, int iterations)
    {
        var random = new Random(seed);
        var directory = Path.GetFullPath(Path.Combine("artifacts", "simulations", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var factory = new TestFactory(Path.Combine(directory, "rivalry.db"));
        var clock = new TestClock { Now = SimulationData.Start };
        var service = new GameService(factory, clock); await service.Initialize();
        var fixture = SimulationData.Create(clock.Now);
        await service.Mutate(s => { s.Players = fixture.Players; s.Games = fixture.Games; s.LastSync = clock.Now; });
        void Check(bool ok, string label) { if (!ok) throw new Exception(label + $" (seed {seed}, database {directory})"); Console.WriteLine("PASS " + label); }
        async Task Reject(Func<Task> action, string label)
        {
            var before = JsonSerializer.Serialize(service.Snapshot());
            try { await action(); } catch (InvalidOperationException) { Check(before == JsonSerializer.Serialize(service.Snapshot()), label + " rolls back atomically"); return; }
            throw new Exception("Expected rejection: " + label);
        }
        var ids = new List<string>();
        // All scoring presets, both formats, and Thursday / Sunday / full-week slates.
        for (var index = 0; index < 18 + iterations; index++)
        {
            clock.Now = SimulationData.Start;
            var format = index % 2 == 0 ? "Snake" : "Salary";
            var ppr = new[] { 0d, .5, 1 }[index / 2 % 3];
            var games = (index / 6 % 3) switch { 0 => fixture.Games.Take(1).ToList(), 1 => fixture.Games.Skip(1).Take(1).ToList(), _ => fixture.Games };
            var id = await service.Create(2030, 6, format, ppr, 200, $"Simulation {index} seed {seed}", games.Select(g => g.Id).ToList()); ids.Add(id);
            Competition Current() => GameService.Find(service.Snapshot(), id);
            var c = Current();
            Check(c.Pool.All(p => games.Any(g => g.Id == p.GameId)) && c.CreatedAt == clock.Now, $"{index}: {format}/{ppr} slate and clock");
            var first = c.Pool.First(p => p.Player.Position == "QB");
            await Reject(() => service.Pick(id, 0, first.Player.Id, "QB", c.Revision), "Not ready");
            await service.Ready(id, 0); await service.Ready(id, 1);
            var undoTested = false;
            for (var pick = 0; pick < 18; pick++)
            {
                c = Current(); var owner = format == "Snake" ? c.Turn : pick < 9 ? 0 : 1;
                var slots = Rules.Slots.Where(slot => !c.Picks.Any(p => p.Owner == owner && p.Slot == slot)).OrderBy(_ => random.Next()).ToList();
                var made = false;
                foreach (var slot in slots)
                {
                    var pool = c.Pool.Where(p => Rules.Fits(slot, p.Player.Position) && !c.Picks.Any(x => x.PlayerId == p.Player.Id && (format == "Snake" || x.Owner == owner))).OrderBy(_ => random.Next()).ToList();
                    foreach (var player in pool)
                    {
                        try { await service.Pick(id, owner, player.Player.Id, slot, c.Revision); made = true; break; }
                        catch (InvalidOperationException) { /* A FLEX choice may leave an unfillable roster. Try another legal choice. */ }
                    }
                    if (made) break;
                }
                Check(made, $"{index}: pick {pick + 1} has a legal completion");
                if (pick == 0 && format == "Snake" && !undoTested)
                {
                    var after = Current();
                    await Reject(() => service.Pick(id, after.Turn, first.Player.Id, "QB", c.Revision), "Stale snake revision");
                    await service.Undo(id, owner, after.Revision);
                    Check(Current().Picks.Count == 1, "Undo requires both participants");
                    await service.Undo(id, 1 - owner, Current().Revision);
                    Check(Current().Picks.Count == 0, "Mutual undo restores turn and pool");
                    pick--; // Replay the undone turn once.
                    undoTested = true;
                }
            }
            // Complete after the one-time undo above, using deterministic positional order.
            while (!Current().DraftComplete)
            {
                c = Current(); var owner = format == "Snake" ? c.Turn : c.Picks.Count(p => p.Owner == 0) < 9 ? 0 : 1;
                var slot = Rules.Slots.First(s => !c.Picks.Any(p => p.Owner == owner && p.Slot == s));
                var player = c.Pool.First(p => Rules.Fits(slot, p.Player.Position) && !c.Picks.Any(x => x.PlayerId == p.Player.Id && (format == "Snake" || x.Owner == owner)));
                await service.Pick(id, owner, player.Player.Id, slot, c.Revision);
            }
            c = Current();
            Check(Enumerable.Range(0, 2).All(o => c.Picks.Where(p => p.Owner == o).Select(p => p.PlayerId).Distinct().Count() == 9 && GameService.Spent(c, o) <= c.Budget), $"{index}: complete valid rosters");
            Check(format != "Snake" || c.Picks.Select(p => p.PlayerId).Distinct().Count() == 18, "Snake players are exclusive");
            clock.Now = c.LockAt.AddTicks(-1); c = Current();
            Check(!c.Locked && Rules.CanViewRoster(c, 0, 1) == (format == "Snake"), "Privacy immediately before kickoff");
            clock.Now = c.LockAt; c = Current();
            Check(c.Locked && Rules.CanViewRoster(c, 0, 1) && Rules.CanViewRoster(c, 1, 0), "Rosters lock and reveal exactly at kickoff");
            await Reject(() => service.Ready(id, 0), "Ready at kickoff");
            await Reject(() => service.Pick(id, 0, first.Player.Id, "QB", c.Revision), "Pick at kickoff");
            await Reject(() => service.Remove(id, 0, c.Picks[0].PlayerId, c.Revision), "Removal at kickoff");
            await Reject(() => service.Undo(id, 0, c.Revision), "Undo at kickoff");
        }
        clock.Now = SimulationData.Start;
        var unfinishedId = await service.Create(2030, 6, "Salary", 1, 200, "Kickoff boundary", fixture.Games.Select(g => g.Id).ToList());
        await service.Ready(unfinishedId, 0); await service.Ready(unfinishedId, 1);
        var unfinished = GameService.Find(service.Snapshot(), unfinishedId);
        var laterPlayer = unfinished.Pool.First(p => p.Player.Position == "QB" && p.Kickoff > unfinished.LockAt);
        clock.Now = unfinished.LockAt.AddTicks(-1);
        await service.Pick(unfinishedId, 0, laterPlayer.Player.Id, "QB", unfinished.Revision);
        Check(GameService.Find(service.Snapshot(), unfinishedId).Picks.Count == 1, "Future-game player can be selected one tick before the first included kickoff");
        clock.Now = unfinished.LockAt;
        await Reject(() => service.Pick(unfinishedId, 1, laterPlayer.Player.Id, "QB", unfinished.Revision), "Otherwise legal Sunday/Monday selection locks at Thursday kickoff");
        clock.Now = fixture.Games.Max(g => g.Kickoff).AddHours(3);
        await service.Mutate(s => {
            foreach (var game in s.Games)
                s.LiveScores.Add(new() { GameId = game.Id, UpdatedAt = clock.Now, Final = true, Stats = s.Players.Where(p => p.Team == game.Home || p.Team == game.Away).Select(p => SimulationData.Performance(p, 2030, 6, true, clock.Now)).ToList() });
        });
        foreach (var id in ids)
        {
            var s = service.Snapshot(); var c = GameService.Find(s, id);
            for (int owner = 0; owner < 2; owner++)
            {
                var expected = c.Picks.Where(p => p.Owner == owner).Sum(p => SimulationData.Expected(c.Pool.First(x => x.Player.Id == p.PlayerId).Player.Position, c.Ppr));
                Check(Math.Abs(GameService.Total(s, c, owner, false) - expected) < .001, "Simulated live totals match independent scoring oracle");
            }
            Check(!GameService.Final(s, c), "Live totals remain provisional even when provider says final");
        }
        var sample = GameService.Find(service.Snapshot(), ids[0]);
        var correctionPick = sample.Picks.First(p => sample.Pool.First(x => x.Player.Id == p.PlayerId).Player.Position == "QB");
        var corrected = correctionPick.PlayerId;
        var beforeScore = GameService.Total(service.Snapshot(), sample, correctionPick.Owner, false);
        await service.Mutate(s => s.LiveScores.SelectMany(b => b.Stats).First(p => p.PlayerId == corrected).PassTd += 1);
        Check(GameService.Total(service.Snapshot(), sample, correctionPick.Owner, false) == beforeScore + 4, "Live stat correction replaces the snapshot score");
        clock.Now = fixture.Games.Max(g => g.Kickoff).AddDays(2).AddSeconds(1);
        await service.Mutate(s => { s.Stats = s.Players.Select(p => SimulationData.Performance(p, 2030, 6)).ToList(); s.LastSync = clock.Now; foreach (var game in s.Games) { game.HomeScore = 24; game.AwayScore = 17; } });
        var restart = new GameService(factory, clock); await restart.Initialize();
        foreach (var id in ids)
        {
            var s = restart.Snapshot(); var c = GameService.Find(s, id);
            Check(c.Locked && c.DraftComplete && GameService.Final(s, c), "Completed game and lock survive SQLite restart");
            Check(GameService.Actual(s, c, c.Picks[0].PlayerId) is { IsLive: false }, "Historical correction supersedes live snapshot after publication buffer");
        }
        var missing = sample.Picks.First(p => sample.Pool.First(x => x.Player.Id == p.PlayerId).Player.Position == "WR").PlayerId;
        await service.Mutate(s => { s.Stats.RemoveAll(p => p.PlayerId == missing); s.LiveScores.Clear(); });
        Check(GameService.Actual(service.Snapshot(), sample, missing) is { DidNotRecordStats: true }, "Missing offensive performance becomes zero only after complete feed coverage");
        Console.WriteLine($"Simulation seed {seed}; {ids.Count} complete competitions; fixture DB {directory}");
    }
}
