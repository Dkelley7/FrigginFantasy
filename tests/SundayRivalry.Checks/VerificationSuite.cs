using System.Diagnostics;
using System.Text.Json;

static class VerificationSuite
{
    public static async Task<int> Run(string[] args)
    {
        int Option(string key, int fallback) { var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? int.Parse(args[index + 1]) : fallback; }
        var seed = Option("--seed", 20261016); var iterations = Option("--iterations", 6);
        if (iterations < 0 || iterations > 1000) throw new ArgumentOutOfRangeException(nameof(iterations));
        var directory = Path.GetFullPath(Path.Combine("artifacts", "verification", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]));
        Directory.CreateDirectory(directory);
        var results = new List<object>(); var failed = 0;
        async Task Group(string name, Func<Task> run)
        {
            Console.WriteLine($"\n=== {name} ==="); var timer = Stopwatch.StartNew(); var original = Console.Out;
            using var log = new StringWriter(); Console.SetOut(new Tee(original, log)); string? error = null;
            try { await run(); } catch (Exception e) { failed++; error = e.ToString(); Console.WriteLine("FAIL " + error); }
            finally { Console.SetOut(original); }
            var output = log.ToString(); await File.WriteAllTextAsync(Path.Combine(directory, name + ".log"), output);
            results.Add(new { name, passed = error is null, checks = output.Split('\n').Count(l => l.StartsWith("PASS ")), milliseconds = timer.ElapsedMilliseconds, error });
        }
        await Group("rules-and-rendering", GameChecks.Run);
        await Group("simulated-seasons", () => LifecycleChecks.Run(seed, iterations));
        await Group("concurrency-and-storage-failures", ConcurrencyChecks.Run);
        await Group("live-feed-and-quotas", Tank01Checks.Run);
        await Group("espn-projections", EspnChecks.Run);
        await Group("accounts-and-restarts", () => AuthChecks.Run());
        if (!args.Contains("--skip-browser")) await Group("two-browser-drafts", DinnerBrowserChecks.Run);
        await File.WriteAllTextAsync(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { seed, iterations, failed, results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"\n{(failed == 0 ? "PASS" : "FAIL")} suite: {failed} failed groups. Seed {seed}. Reports: {directory}");
        return failed == 0 ? 0 : 1;
    }
    sealed class Tee(TextWriter console, TextWriter log) : TextWriter
    {
        public override System.Text.Encoding Encoding => console.Encoding;
        public override void Write(char value) { console.Write(value); log.Write(value); }
        public override void WriteLine(string? value) { console.WriteLine(value); log.WriteLine(value); }
    }
}
