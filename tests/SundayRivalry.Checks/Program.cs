using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SundayRivalry.Domain;
using SundayRivalry.Services;

if (args.Contains("--suite")) { Environment.ExitCode = await VerificationSuite.Run(args); return; }
if (args.Contains("--salary-audit")) { await SalaryAudit.Run(args); return; }
if (args.Contains("--espn-preview")) { await EspnChecks.Preview(); return; }
if (args.Contains("--dinner-browser")) { await DinnerBrowserChecks.Run(); return; }
if (args.Contains("--browser")) { await BrowserChecks.Run(); return; }
if (args.Contains("--auth")) { await AuthChecks.Run(); return; }
if (args.Contains("--auth-browser")) { await AuthChecks.Run(true); return; }
if (args.Contains("--tank01")) { await Tank01Checks.Run(); return; }
await GameChecks.Run();
sealed class TestClock : TimeProvider
{
    long ticks = DateTimeOffset.UtcNow.UtcTicks;
    public DateTimeOffset Now { get => new(Interlocked.Read(ref ticks), TimeSpan.Zero); set => Interlocked.Exchange(ref ticks, value.UtcTicks); }
    public override DateTimeOffset GetUtcNow() => Now;
}
sealed class TestFactory(string path) : IDbContextFactory<StateDb>
{
    public StateDb CreateDbContext() => new(new DbContextOptionsBuilder<StateDb>().UseSqlite($"Data Source={path}").Options);
}
sealed class UnusedHttpFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }
