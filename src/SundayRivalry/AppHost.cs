using SundayRivalry.Components;
using SundayRivalry.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using System.Threading.RateLimiting;

namespace SundayRivalry;
public static class AppHost
{
    public static async Task<WebApplication> Create(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ApplicationName = typeof(AppHost).Assembly.GetName().Name });
        builder.WebHost.UseStaticWebAssets();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        var dataDirectory = builder.Configuration["DataDirectory"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dataDirectory);
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys"))).SetApplicationName("SundayRivalry");
        builder.Services.AddDbContextFactory<StateDb>(o => o.UseSqlite($"Data Source={Path.Combine(dataDirectory, "rivalry.db")}"));
        builder.Services.AddDbContext<AccountsDb>(o => o.UseSqlite($"Data Source={Path.Combine(dataDirectory, "accounts.db")}"));
        builder.Services.AddIdentity<PlayerAccount, IdentityRole>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 12;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequireDigit = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireLowercase = false;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        }).AddEntityFrameworkStores<AccountsDb>().AddClaimsPrincipalFactory<PlayerClaimsFactory>();
        builder.Services.ConfigureApplicationCookie(o =>
        {
            o.LoginPath = "/login";
            o.Cookie.Name = ".SundayRivalry.Accounts";
            o.ExpireTimeSpan = TimeSpan.FromDays(14);
            o.SlidingExpiration = true;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
        });
        builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
        // Trust the HTTPS forwarding header only from ASP.NET's default loopback proxies,
        // as used by local Tailscale Serve. Do not trust arbitrary internet proxies.
        builder.Services.Configure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.XForwardedProto);
        builder.Services.AddSingleton<AccountRegistration>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<GameService>();
        builder.Services.AddSingleton<DataSync>();
        builder.Services.AddSingleton<EspnProjections>();
        builder.Services.AddHttpClient("espn-projections", c => { c.Timeout = TimeSpan.FromSeconds(30); c.MaxResponseContentBufferSize = 8 * 1024 * 1024; })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        builder.Services.AddHostedService<EspnProjectionWorker>();
        builder.Services.AddHttpClient("nflverse", c => { c.Timeout = TimeSpan.FromSeconds(45); c.DefaultRequestHeaders.UserAgent.ParseAdd("SundayRivalry/1.0 (personal-fantasy-app)"); });
        builder.Services.AddHostedService<SyncWorker>();
        builder.Services.AddSingleton(new LiveFeedPaths(dataDirectory));
        builder.Services.AddSingleton<Tank01Ledger>();
        builder.Services.AddSingleton<Tank01Live>();
        builder.Services.AddHttpClient("tank01", c => { c.Timeout = TimeSpan.FromSeconds(20); c.MaxResponseContentBufferSize = 8 * 1024 * 1024; })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        builder.Services.AddHostedService<Tank01Worker>();
        builder.Services.AddAuthorization();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<AuthenticationStateProvider, PlayerAuthenticationStateProvider>();
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy("registration", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
        });
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        configure?.Invoke(builder);
        var app = builder.Build();
        await app.Services.GetRequiredService<GameService>().Initialize();
        var gameStore = app.Services.GetRequiredService<GameService>();
        if (gameStore.Snapshot().Espn.LeagueId == 0 && builder.Configuration.GetValue<long>("Espn:LeagueId") is > 0 and var espnLeague)
        {
            var nextGame = gameStore.Snapshot().Games.Where(g => g.Kickoff > app.Services.GetRequiredService<TimeProvider>().GetUtcNow()).OrderBy(g => g.Kickoff).FirstOrDefault();
            if (nextGame is not null) await gameStore.Mutate(s => { s.Espn.LeagueId = espnLeague; s.Espn.Season = nextGame.Season; s.Espn.Week = nextGame.Week; });
        }
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AccountsDb>().Database.EnsureCreatedAsync();
        bool auth = builder.Configuration.GetValue("Auth:Enabled", true);
        app.UseForwardedHeaders();
        // Record the route and outcome only; never log credentials, cookies, or invite queries.
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "/";
            context.Response.OnStarting(() =>
            {
                var status = context.Response.StatusCode;
                if (path is "/login" or "/register" || status >= 400)
                    app.Logger.LogInformation("Request {Method} {Path} returned {Status}; scheme={Scheme}; signedIn={SignedIn}; trace={Trace}",
                        context.Request.Method, path, status, context.Request.Scheme,
                        context.User.Identity?.IsAuthenticated == true, context.TraceIdentifier);
                return Task.CompletedTask;
            });
            await next();
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.Use(async (context, next) =>
        {
            if (!auth && !builder.Configuration.GetValue<bool>("AllowLan") && context.Connection.RemoteIpAddress is { } ip && !System.Net.IPAddress.IsLoopback(ip))
            { context.Response.StatusCode = 403; await context.Response.WriteAsync("Localhost mode. Configure private accounts or enable trusted home-network access."); return; }
            var path = context.Request.Path;
            bool accountPublic = path == "/login" || path == "/register" || path == "/account.css" || path == "/account.js" || path == "/favicon.svg" || path == "/football.svg";
            if (auth && context.User.Identity?.IsAuthenticated != true && !accountPublic)
            { context.Response.Redirect("/login"); return; }
            await next();
        });
        if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseAntiforgery();
        app.MapAccountPages();
        app.MapStaticAssets(builder.Configuration["StaticAssetsManifest"]);
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        return app;

    }
}
