using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using SundayRivalry.Domain;
using SundayRivalry.Services;

static class DinnerBrowserChecks
{
    public static async Task Run()
    {
        await using var host = new TestHost(); await host.Start();
        var fixture = SimulationData.Create(host.Clock.Now);
        var injuryPlayer = fixture.Players.First(p => p.Team == "BUF" && p.Position == "QB");
        await host.Games.Mutate(s => { s.Games = fixture.Games; s.Players = fixture.Players; s.LastSync = host.Clock.Now; s.Injuries.Add(new() { PlayerId = injuryPlayer.Id, Designation = "Questionable", UpdatedAt = host.Clock.Now }); s.Espn.InjuriesUpdatedAt = host.Clock.Now; });
        var password = "Dinner simulation " + Guid.NewGuid().ToString("N");
        using var first = host.Client(); using var second = host.Client();
        async Task<HttpResponseMessage> Post(HttpClient client, string page, string endpoint, Dictionary<string,string> fields)
        {
            var html = await client.GetStringAsync(page);
            fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
            return await client.PostAsync(endpoint, new FormUrlEncodedContent(fields));
        }
        Dictionary<string,string> Registration(string email, string name, string token = "") => new() { ["email"] = email, ["name"] = name, ["password"] = password, ["confirmPassword"] = password, ["invite"] = token };
        var created = await Post(first, "/register", "/register", Registration("one@example.com", "Home"));
        if (created.StatusCode != HttpStatusCode.Found) throw new Exception("Could not create browser fixture account");
        var invitation = await Post(first, "/account", "/account/invite", new() { ["email"] = "two@example.com" });
        var link = WebUtility.HtmlDecode(Regex.Match(await invitation.Content.ReadAsStringAsync(), "id=\"invite-url\" value=\"([^\"]+)\"").Groups[1].Value);
        var invited = await Post(second, link, "/register", Registration("two@example.com", "Away", Uri.UnescapeDataString(link.Split("invite=")[1])));
        if (invited.StatusCode != HttpStatusCode.Found) throw new Exception("Could not create second browser fixture account");
        await using var chrome = new HeadlessChrome(host.Directory); await chrome.Start();
        using var home = await chrome.Tab(); using var away = await chrome.Tab();
        var tabs = new[] { home, away };
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); }
        try
        {
            for (var owner = 0; owner < 2; owner++)
            {
                var tab = tabs[owner];
                await tab.Command("Page.navigate", new { url = new Uri(host.Address, "login").ToString() });
                await tab.Wait("document.querySelector('[name=email]') !== null");
                await tab.Eval($"document.querySelector('[name=email]').value={JsonSerializer.Serialize(owner == 0 ? "one@example.com" : "two@example.com")};document.querySelector('[name=password]').value={JsonSerializer.Serialize(password)};document.querySelector('form').requestSubmit()");
                await tab.Wait("document.querySelector('a.account-menu') !== null");
            }
            Check(true, "Two independent browser sessions sign in through actual forms");
            foreach (var format in new[] { "Snake", "Salary" })
            {
                await home.Click("New competition");
                await home.Wait("document.querySelector('input[placeholder=\"The Sunday showdown\"]') !== null");
                await home.Eval($"(()=>{{const input=document.querySelector('input[placeholder=\"The Sunday showdown\"]');input.value={JsonSerializer.Serialize("Dinner " + format)};input.dispatchEvent(new Event('change',{{bubbles:true}}))}})()");
                if (format == "Salary") await home.Click("Salary cap");
                await home.Click("Thursday"); await home.Click("Monday");
                await home.Click("Create competition");
                await home.Wait("document.body.innerText.includes('Meet in the draft room')");
                var id = host.Games.Snapshot().Competitions.Last().Id;
                Competition Current() => GameService.Find(host.Games.Snapshot(), id);
                Check(Current().Format == format && Current().Games.Count == 3, "Browser creates " + format + " competition with Thursday, Sunday and Monday games");
                foreach (var tab in tabs) { await tab.Click("Overview"); await tab.Click("Dinner " + format); await tab.Wait("document.body.innerText.includes('Meet in the draft room')"); }
                await home.Eval("(()=>{const b=document.querySelectorAll('.ready-actions button')[1];b.disabled=false;b.click()})()");
                await home.Wait("document.body.innerText.includes('You can only act for your signed-in participant')");
                Check(!Current().Ready[1], "Browser tampering cannot mark the rival ready");
                await home.Eval("document.querySelector('[aria-label=\"Dismiss error\"]').click()");
                for (var owner = 0; owner < 2; owner++)
                    await tabs[owner].Eval($"Array.from(document.querySelectorAll('.ready-actions button'))[{owner}].click()");
                foreach (var tab in tabs) await tab.Wait("document.querySelector('.lobby') === null");
                for (var pick = 0; pick < 18; pick++)
                {
                    var c = Current(); var owner = format == "Snake" ? c.Turn : pick < 9 ? 0 : 1;
                    var slot = Rules.Slots.First(s => !c.Picks.Any(p => p.Owner == owner && p.Slot == s));
                    var player = c.Pool.First(p => Rules.Fits(slot, p.Player.Position) && !c.Picks.Any(x => x.PlayerId == p.Player.Id && (format == "Snake" || x.Owner == owner)));
                    var tab = tabs[owner];
                    var selector = JsonSerializer.Serialize("button[aria-label=\"Select " + player.Player.Name + "\"]");
                    await tab.Wait($"document.querySelector({selector})?.disabled === false");
                    await tab.Eval($"document.querySelector({selector}).click()");
                    await tab.Wait("document.querySelector('[role=dialog]') !== null");
                    if (pick == 0) Check((await tab.Eval("document.querySelector('.modal .injury-badge') !== null")).GetBoolean(), "Selection dialog shows the player's current injury designation");
                    await tab.Click(format == "Snake" ? "Make the pick" : "Add to lineup");
                    await tab.Wait("document.querySelector('[role=dialog]') === null");
                    if (Current().Picks.Count != pick + 1) throw new Exception("Browser pick failed: " + (await tab.Eval("document.body.innerText")).GetString());
                    if (format == "Snake")
                        await tabs[1 - owner].Wait($"Array.from(document.querySelectorAll('.rosters-column strong')).some(e=>e.textContent==={JsonSerializer.Serialize(player.Player.Name)})");
                    if (pick == 0 && format == "Snake")
                    {
                        await host.Games.Mutate(s => s.Injuries.Single(i => i.PlayerId == injuryPlayer.Id).Designation = "Out");
                        foreach (var client in tabs) await client.Wait("document.querySelector('.rosters-column .injury-badge')?.textContent === 'Out'");
                        foreach (var client in tabs) await client.Wait("document.querySelector('.rosters-column .roster-points')?.textContent === '0.0'");
                        Check(true, "Current injury changes reach both browsers and drafted rosters without reload");
                    }
                }
                Check(Current().DraftComplete, $"Two browsers complete all 18 {format} picks");
                if (format == "Salary")
                {
                    Check(Current().Picks.Where(p => p.Owner == 0).Select(p => p.PlayerId).Intersect(Current().Picks.Where(p => p.Owner == 1).Select(p => p.PlayerId)).Count() == 9, "Salary browsers share players and pick nine times consecutively");
                    foreach (var tab in tabs)
                    {
                        await tab.Click("Pick history"); await tab.Wait("document.querySelectorAll('.log-row').length === 9");
                        await tab.Click("Matchup & points"); await tab.Wait("document.querySelector('.matchup-score').innerText.includes('Hidden')");
                        await tab.Click("Draft room");
                    }
                    Check(true, "Both salary browsers hide opponent history and scores before kickoff");
                    var removed = Current().Picks.First(p => p.Owner == 0);
                    var candidate = Current().Pool.First(p => p.Player.Id == removed.PlayerId);
                    var removeSelector = JsonSerializer.Serialize("button[aria-label=\"Remove " + candidate.Player.Name + "\"]");
                    await home.Eval($"document.querySelector({removeSelector}).click()");
                    await home.Wait("document.querySelector('.budget-average').innerText.includes('1 positions left')");
                    Check(Current().Picks.Count == 17, "Salary browser removes a player and restores its budget average");
                    var selectSelector = JsonSerializer.Serialize("button[aria-label=\"Select " + candidate.Player.Name + "\"]");
                    await home.Eval($"document.querySelector({selectSelector}).click()"); await home.Click("Add to lineup");
                    await home.Wait("document.querySelector('.budget-average').innerText.includes('Lineup set')");
                }
                await home.Command("Emulation.setDeviceMetricsOverride", new { width = 390, height = 844, deviceScaleFactor = 1, mobile = true });
                Check((await home.Eval("document.documentElement.scrollWidth <= innerWidth")).GetBoolean(), format + " draft fits a phone viewport");
                await home.Screenshot(Path.Combine(host.Directory, format + "-mobile.png"));
                await home.Command("Emulation.clearDeviceMetricsOverride");
            }
            await host.Advance(fixture.Games[0].Kickoff);
            foreach (var tab in tabs)
            {
                await tab.Wait("document.body.innerText.includes('Rosters locked')");
                await tab.Click("Pick history"); await tab.Wait("document.querySelectorAll('.log-row').length === 18");
                await tab.Click("Matchup & points"); await tab.Wait("!document.querySelector('.matchup-score').innerText.includes('Hidden')");
            }
            Check(true, "Kickoff reveals salary rosters and history in both browsers without reload");
            await host.Games.Mutate(s => {
                foreach (var game in s.Games) s.LiveScores.Add(new() { GameId = game.Id, UpdatedAt = host.Clock.Now, Stats = s.Players.Where(p => p.Team == game.Home || p.Team == game.Away).Select(p => SimulationData.Performance(p, game.Season, game.Week, true, host.Clock.Now)).ToList() });
            });
            var salary = host.Games.Snapshot().Competitions.Last();
            var expected = GameService.Total(host.Games.Snapshot(), salary, 0, false).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            foreach (var tab in tabs) await tab.Wait($"document.querySelector('.matchup-score').innerText.includes({JsonSerializer.Serialize(expected)})");
            Check(true, "Simulated live scores reach both connected browsers");
            await home.Command("Page.reload"); await home.Wait("document.querySelector('a.account-menu') !== null"); await home.Click("Dinner Salary");
            Check(host.Games.Snapshot().Competitions.Last().Picks.Count == 18, "Browser reload preserves authentication and completed rosters");
            await home.Click("Data & settings");
            await home.Wait("document.querySelector('.espn-projections') !== null");
            await home.Command("Emulation.setDeviceMetricsOverride", new { width = 390, height = 844, deviceScaleFactor = 1, mobile = true });
            Check((await home.Eval("document.documentElement.scrollWidth <= innerWidth")).GetBoolean(), "ESPN connection settings fit a phone viewport");
            Check(home.Errors.Count == 0 && away.Errors.Count == 0, "No JavaScript exceptions during both drafts and scoring");
        }
        catch
        {
            for (int i = 0; i < tabs.Length; i++)
                try { await tabs[i].Screenshot(Path.Combine(host.Directory, $"failure-{i}.png")); await File.WriteAllTextAsync(Path.Combine(host.Directory, $"failure-{i}.txt"), (await tabs[i].Eval("document.body.innerText")).GetString()); } catch { }
            Console.WriteLine("Browser failure artifacts: " + host.Directory); throw;
        }
    }
}

sealed class HeadlessChrome(string directory) : IAsyncDisposable
{
    Process? process; string endpoint = ""; readonly Cdp root = new(); readonly HttpClient http = new();
    public async Task Start()
    {
        var executable = Environment.GetEnvironmentVariable("SUNDAY_RIVALRY_CHROME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Chrome is required for browser checks. Set SUNDAY_RIVALRY_CHROME or use -SkipBrowser.", executable);
        var profile = Path.Combine(directory, "chrome");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0", "--user-data-dir=" + profile, "--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1, EXCLUDE localhost", "about:blank" }) start.ArgumentList.Add(argument);
        process = Process.Start(start)!;
        var portFile = Path.Combine(profile, "DevToolsActivePort");
        for (int i = 0; i < 100 && !File.Exists(portFile); i++) { if (process.HasExited) throw new Exception("Test Chrome exited early"); await Task.Delay(100); }
        var port = (await File.ReadAllLinesAsync(portFile))[0]; endpoint = "http://127.0.0.1:" + port;
        var version = JsonDocument.Parse(await http.GetStringAsync(endpoint + "/json/version"));
        await root.Connect(version.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!);
    }
    public async Task<Cdp> Tab(CookieContainer? cookies = null, Uri? address = null)
    {
        var context = (await root.Command("Target.createBrowserContext")).GetProperty("browserContextId").GetString();
        if (cookies is not null && address is not null)
            await root.Command("Storage.setCookies", new { browserContextId = context, cookies = cookies.GetCookies(address).Cast<Cookie>().Select(c => new { name = c.Name, value = c.Value, domain = address.Host, path = c.Path, httpOnly = c.HttpOnly, secure = false }).ToArray() });
        var id = (await root.Command("Target.createTarget", new { url = "about:blank", browserContextId = context })).GetProperty("targetId").GetString();
        await Task.Delay(500);
        var list = JsonDocument.Parse(await http.GetStringAsync(endpoint + "/json/list"));
        var tab = new Cdp(); await tab.Connect(list.RootElement.EnumerateArray().First(t => t.GetProperty("id").GetString() == id).GetProperty("webSocketDebuggerUrl").GetString()!);
        await tab.Command("Runtime.enable"); await tab.Command("Page.enable"); return tab;
    }
    public async ValueTask DisposeAsync()
    {
        try { if (process is { HasExited: false }) { await root.Command("Browser.close"); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await process.WaitForExitAsync(timeout.Token); } }
        finally { if (process is { HasExited: false }) process.Kill(true); process?.Dispose(); root.Dispose(); http.Dispose(); }
    }
}
