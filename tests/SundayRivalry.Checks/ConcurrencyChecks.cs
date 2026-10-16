using Microsoft.EntityFrameworkCore;
using SundayRivalry.Domain;
using SundayRivalry.Services;
using System.Text.Json;

static class ConcurrencyChecks
{
    public static async Task Run()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "concurrency", Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(directory);
        var factory = new FaultFactory(new TestFactory(Path.Combine(directory, "state.db")));
        var clock = new TestClock { Now = SimulationData.Start }; var store = new GameService(factory, clock); await store.Initialize();
        var fixture = SimulationData.Create(clock.Now);
        await store.Mutate(s => { s.Games = fixture.Games; s.Players = fixture.Players; });
        var notifications = 0; store.Changed += () => notifications++;
        void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var auditPool = new[] { "QB", "RB", "RB", "WR", "WR", "TE", "RB", "K", "D/ST" }.Select((position, i) => new Candidate { Player = new() { Id = "audit-" + i, Position = position }, Projection = 10, Salary = 10 }).ToList();
        auditPool.Add(new() { Player = new() { Id = "premium-qb", Position = "QB" }, Projection = 20, Salary = 30 });
        auditPool.Add(new() { Player = new() { Id = "cheap-rb", Position = "RB" }, Projection = 1, Salary = 1 });
        Check(SalaryAudit.Optimize(auditPool, false) is { Points: 100, Cost: 110 }, "Salary audit finds the highest projected legal roster without reusing FLEX players");
        Check(SalaryAudit.Optimize(auditPool, true) is { Cost: 81 }, "Salary audit finds the cheapest legal roster");
        Check(SalaryAudit.Optimize(auditPool.Take(8).ToList(), false) is null, "Salary audit reports an unfillable slate instead of inventing a roster");
        async Task<bool> Attempt(Func<Task> action) { try { await action(); return true; } catch (InvalidOperationException) { return false; } }
        async Task<string> New()
        {
            var id = await store.Create(2030, 6, "Salary", 1, 200, "Race fixture", fixture.Games.Select(g => g.Id).ToList());
            await store.Ready(id, 0); await store.Ready(id, 1); return id;
        }
        var id = await New();
        await store.Mutate(s => { var c = GameService.Find(s, id); c.Pool.First(p => p.Player.Position == "QB").Salary = 120; c.Pool.First(p => p.Player.Position == "WR").Salary = 120; });
        var c = GameService.Find(store.Snapshot(), id); var qb = c.Pool.First(p => p.Player.Position == "QB"); var wr = c.Pool.First(p => p.Player.Position == "WR");
        var results = await Task.WhenAll(Attempt(() => store.Pick(id, 0, qb.Player.Id, "QB", c.Revision)), Attempt(() => store.Pick(id, 0, wr.Player.Id, "WR1", c.Revision)));
        Check(results.Count(x => x) == 1 && GameService.Find(store.Snapshot(), id).Picks.Count == 1, "Concurrent purchases from two tabs cannot overspend one salary roster");
        id = await New(); c = GameService.Find(store.Snapshot(), id); qb = c.Pool.First(p => p.Player.Position == "QB");
        results = await Task.WhenAll(Attempt(() => store.Pick(id, 0, qb.Player.Id, "QB", c.Revision)), Attempt(() => store.Pick(id, 1, qb.Player.Id, "QB", c.Revision)));
        Check(results.All(x => x) && GameService.Find(store.Snapshot(), id).Picks.Count == 2, "Concurrent rival salary selections both succeed with a shared player and stale global revision");
        var snapshot = store.Snapshot(); snapshot.Names[0] = "Tampered snapshot"; snapshot.Competitions.Clear();
        Check(store.Snapshot().Names[0] != "Tampered snapshot" && store.Snapshot().Competitions.Count == 2, "Client snapshots cannot mutate server state");
        var before = JsonSerializer.Serialize(store.Snapshot()); var notified = notifications;
        factory.Fail = true;
        var accepted = await Attempt(() => store.Remove(id, 0, qb.Player.Id, c.Revision));
        factory.Fail = false;
        Check(!accepted && before == JsonSerializer.Serialize(store.Snapshot()) && notifications == notified, "Database write failure publishes neither a partial roster nor a change notification");
        var restarted = new GameService(factory, clock); await restarted.Initialize();
        Check(before == JsonSerializer.Serialize(restarted.Snapshot()), "Database failure leaves persisted state identical after restart");
        await store.Remove(id, 0, qb.Player.Id, c.Revision);
        Check(GameService.Find(store.Snapshot(), id).Picks.Count == 1, "Draft writer recovers after database failure and releases its lock");
    }
    sealed class FaultFactory(IDbContextFactory<StateDb> inner) : IDbContextFactory<StateDb>
    {
        public bool Fail;
        public StateDb CreateDbContext() => Fail ? throw new InvalidOperationException("Simulated database unavailable") : inner.CreateDbContext();
    }
}
