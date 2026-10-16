using SundayRivalry.Domain;
using SundayRivalry.Services;

static class ProjectionChecks
{
    public static async Task Run()
    {
        var factory = new TestFactory(Path.Combine(Path.GetTempPath(), "SundayRivalryChecks", "projections-" + Guid.NewGuid().ToString("N") + ".db"));
        var store = new GameService(factory, TimeProvider.System); await store.Initialize();
        var games = DemoData.Games(DateTimeOffset.UtcNow); var players = DemoData.Players();
        await store.Mutate(s => { s.Games = games; s.Players = players; });
        var checks = 0;
        void Check(bool valid, string label) { if (!valid) throw new Exception("FAILED " + label); Console.WriteLine("PASS " + label); checks++; }
        async Task<string> Create(double ppr = 1) => await store.Create(games[0].Season, 1, "Salary", ppr, 200, "Synthetic projection check", games.Select(g => g.Id).ToList());
        Competition Current(string id) => GameService.Find(store.Snapshot(), id);
        var existing = await Create(); var oldProjection = Current(existing).Pool.First(p => p.Player.Id == "demo-KC-Carter").Projection;
        ProjectionImport.Preview Preview(string csv) => ProjectionImport.Build(DataSync.Csv(csv), players, "Player", "Team", "Pos", "FPTS");
        // These fixtures are synthetic; no FanDuel export has been assumed or fetched.
        var preview = Preview("Player,Team,Pos,FPTS\nAlex Carter,KC,QB,22.2\nUnknown Player,KC,WR,10.5\nJordan Carter,BUF,QB,NaN\n");
        Check(preview.Matches.Count == 1 && preview.Issues.Count == 2, "Projection preview reports unmatched players and rejects non-finite points");
        await ProjectionImport.Save(store, preview, games[0].Season, 1, 1);
        var created = await Create(); var candidate = Current(created).Pool.First(p => p.Player.Id == "demo-KC-Carter");
        Check(candidate.Projection == 22.2 && candidate.ProjectionSource == "FanDuel Research" && candidate.ProjectionImportedAt.HasValue, "New competition uses matched weekly projection with source and timestamp");
        Check(Current(existing).Pool.First(p => p.Player.Id == candidate.Player.Id).Projection == oldProjection, "Import preserves estimates in existing competitions");
        Check(Current(created).Pool.First(p => p.Player.Id == "demo-BUF-Carter").ProjectionSource == "", "Unmatched players retain explicitly labeled local estimates");
        var otherScoring = await Create(.5);
        Check(Current(otherScoring).Pool.First(p => p.Player.Id == candidate.Player.Id).ProjectionSource == "", "Full PPR projections do not leak into half-PPR competitions");
        var otherWeek = new Competition { Season = games[0].Season, Week = 2, Games = games };
        Check(GameService.BuildPool(players, [], otherWeek, store.Snapshot().Projections).All(p => p.ProjectionSource == ""), "Projection sets apply only to their declared NFL week");
        await ProjectionImport.Save(store, Preview("Player,Team,Pos,FPTS\nAlex Brooks,KC,RB,14.3\n"), games[0].Season, 1, 1);
        Check(store.Snapshot().Projections.Single().Players.Count == 2, "Separate position exports merge without discarding previously imported players");
        await ProjectionImport.Save(store, Preview("Player,Team,Pos,FPTS\nAlex Carter,KC,QB,24.1\n"), games[0].Season, 1, 1);
        var saved = store.Snapshot().Projections.Single();
        Check(saved.Players.Count == 2 && saved.Players.Single(p => p.PlayerId == candidate.Player.Id).Points == 24.1 && Current(created).Pool.First(p => p.Player.Id == candidate.Player.Id).Projection == 22.2, "Reimport updates cached projections while preserving frozen competition values");
        var duplicateRejected = false;
        try { Preview("Player,Team,Pos,FPTS\nAlex Carter,KC,QB,22\nAlex Carter,KC,QB,23\n"); }
        catch (InvalidDataException) { duplicateRejected = true; }
        Check(duplicateRejected, "Conflicting duplicate player projections cannot be imported");
        var defense = Preview("Player,Team,Pos,FPTS\nKansas City Chiefs,KC,DST,8.4\n");
        Check(defense.Matches.Single().PlayerId == "demo-KC-Defense", "Team defense matches by team and position instead of display-name variations");
        var restarted = new GameService(factory, TimeProvider.System); await restarted.Initialize();
        Check(restarted.Snapshot().Projections.Single().Players.Count == 2, "Imported projection sets persist through SQLite restart");
        Console.WriteLine($"\n{checks} projection checks passed.");
    }
}
