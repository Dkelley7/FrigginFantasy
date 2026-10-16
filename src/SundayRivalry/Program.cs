using SundayRivalry;
using SundayRivalry.Services;
var app = await AppHost.Create(args);
var importIndex = Array.IndexOf(args, "--import-dir");
if (importIndex >= 0 && args.Length > importIndex + 1)
{
    await app.Services.GetRequiredService<DataSync>().ImportDirectory(args[importIndex + 1]);
    Console.WriteLine("NFL data imported into SQLite."); return;
}
var syncIndex = Array.IndexOf(args, "--sync-season");
if (args.Contains("--validate-tank01"))
{
    Console.WriteLine(await app.Services.GetRequiredService<Tank01Live>().Validate()); return;
}
if (syncIndex >= 0 && args.Length > syncIndex + 1 && int.TryParse(args[syncIndex + 1], out var syncSeason))
{
    await app.Services.GetRequiredService<DataSync>().Sync(syncSeason);
    Console.WriteLine(app.Services.GetRequiredService<GameService>().Snapshot().SyncMessage); return;
}
await app.RunAsync();
