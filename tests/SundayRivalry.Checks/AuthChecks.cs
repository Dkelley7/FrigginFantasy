using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SundayRivalry.Services;

static class AuthChecks
{
    public static async Task Run(bool browser = false)
    {
        await using var host = new TestHost(); await host.Start();
        var directory = host.Directory;
        var games = host.Games;
        await games.Mutate(s => s.Names[0] = "Original player");
        var oldId = await games.Create(2026, 1, "Snake", 1, 200, "Existing competition", [], true);
        var password = "Private test passphrase " + Guid.NewGuid().ToString("N");
        var newPassword = "Changed test passphrase " + Guid.NewGuid().ToString("N");
        var cookieJars = new Dictionary<HttpClient, CookieContainer>();
        HttpClient Client()
        {
            var jar = new CookieContainer();
            var client = host.Client(jar);
            cookieJars[client] = jar; return client;
        }
        using var first = Client(); using var second = Client(); using var outsider = Client();
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception("FAILED " + label); Console.WriteLine("PASS " + label); checks++; }
        async Task<Dictionary<string, string>> Form(HttpClient client, string path, Dictionary<string, string> fields)
        {
            var html = await client.GetStringAsync(path);
            fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
            if (fields["__RequestVerificationToken"].Length < 10) throw new Exception("Missing antiforgery field at " + path);
            return fields;
        }
        async Task<HttpResponseMessage> Post(HttpClient client, string page, string endpoint, Dictionary<string, string> fields) => await client.PostAsync(endpoint, new FormUrlEncodedContent(await Form(client, page, fields)));
        Dictionary<string, string> Registration(string email, string invite = "", string name = "David", string? pass = null) => new() { ["email"] = email, ["name"] = name, ["password"] = pass ?? password, ["confirmPassword"] = pass ?? password, ["invite"] = invite };
        Dictionary<string, string> Login(string email, string pass) => new() { ["email"] = email, ["password"] = pass };
        try
        {

            Check((await first.GetAsync("/")).Headers.Location?.ToString() == "/login", "Accounts are required by default");
            Check((await first.GetStringAsync("/login")).Contains("Create your account"), "First-run screen offers in-app account creation");
            Check((await first.GetAsync("/account.css")).StatusCode == HttpStatusCode.OK, "Anonymous account screens can load their styles");
            var blockedRegistration = await outsider.PostAsync("/register", new FormUrlEncodedContent(Registration("first.player@example.com")));
            Check((await blockedRegistration.Content.ReadAsStringAsync()).Contains("Continue safely") && (await first.GetStringAsync("/login")).Contains("Create your account"), "Registration without antiforgery token offers recovery and creates no account");
            Check((await Post(first, "/register", "/register", Registration("first.player@example.com", pass: "short"))).StatusCode == HttpStatusCode.BadRequest, "Short registration password is rejected");
            var mismatch = Registration("first.player@example.com"); mismatch["confirmPassword"] = "different passphrase";
            Check((await Post(first, "/register", "/register", mismatch)).StatusCode == HttpStatusCode.BadRequest, "Registration confirms matching passwords");
            if (browser) await Browser(host, cookieJars[first], async tab =>
            {
                await tab.Command("Page.navigate", new { url = new Uri(host.Address, "register").ToString() });
                await tab.Wait("document.body.innerText.includes('Claim your home field')");
                await tab.Screenshot("artifacts/account-register-desktop.png");
                await tab.Command("Emulation.setDeviceMetricsOverride", new { width = 390, height = 844, deviceScaleFactor = 1, mobile = true });
                await tab.Screenshot("artifacts/account-register-mobile.png");
                Check((await tab.Eval("document.documentElement.scrollWidth <= window.innerWidth")).GetBoolean(), "Account registration fits a phone screen");
            });
            var create = await Post(first, "/register", "/register", Registration("first.player@example.com"));
            Check(create.Headers.Location?.ToString() == "/account?welcome=1", "Gmail address and app password create the first account and sign in");
            Check((await first.GetStringAsync("/")).Contains("PRIVATE LEAGUE"), "New account can access the league");
            Check((await Post(outsider, "/register", "/register", Registration("second.player@example.com"))).StatusCode == HttpStatusCode.BadRequest, "Second player cannot register without an invitation");
            var inviteResponse = await Post(first, "/account", "/account/invite", new() { ["email"] = "second.player@example.com" });
            var inviteHtml = await inviteResponse.Content.ReadAsStringAsync();
            var link = WebUtility.HtmlDecode(Regex.Match(inviteHtml, "id=\"invite-url\" value=\"([^\"]+)\"").Groups[1].Value);
            Check(link.StartsWith("/register?invite="), "First player creates a private invitation link");
            var token = Uri.UnescapeDataString(link.Split("invite=")[1]);
            Check((await Post(outsider, link, "/register", Registration("outsider@example.com", token))).StatusCode == HttpStatusCode.BadRequest, "Invitation is bound to the intended email address");
            var form1 = await Form(second, link, Registration("second.player@example.com", token, "Your rival"));
            using var racing = Client();
            var form2 = await Form(racing, link, Registration("second.player@example.com", token, "Your rival"));
            var results = await Task.WhenAll(second.PostAsync("/register", new FormUrlEncodedContent(form1)), racing.PostAsync("/register", new FormUrlEncodedContent(form2)));
            Check(results.Count(r => r.StatusCode == HttpStatusCode.Found) == 1, "Concurrent invitation redemption creates exactly one second account");
            using var db = new AccountsDb(new DbContextOptionsBuilder<AccountsDb>().UseSqlite($"Data Source={Path.Combine(directory, "accounts.db")}").Options);
            var accounts = await db.Users.OrderBy(u => u.PlayerSlot).ToListAsync();
            Check(accounts.Count == 2 && accounts[0].PlayerSlot == 0 && accounts[1].PlayerSlot == 1, "Each account owns one distinct roster slot");
            Check(accounts.All(u => u.PasswordHash != password && u.PasswordHash is { Length: > 30 }), "Database contains password hashes rather than plaintext passwords");
            Check(await db.Invitations.CountAsync(i => i.UsedAt != null) == 1, "Invitation is consumed once registration succeeds");
            Check((await Post(outsider, "/register", "/register", Registration("third.player@example.com", token))).StatusCode == HttpStatusCode.BadRequest, "Registration closes once both accounts exist");
            await games.Initialize(); var snapshot = games.Snapshot();
            Check(snapshot.Competitions.Any(c => c.Id == oldId && c.Names[0] == "Original player") && snapshot.Names[0] == "David", "Registration preserves existing competitions while updating new-competition names");
            var blockedLogin = await outsider.PostAsync("/login", new FormUrlEncodedContent(Login("first.player@example.com", password)));
            Check(blockedLogin.StatusCode == HttpStatusCode.OK && (await blockedLogin.Content.ReadAsStringAsync()).Contains("session changed") && (await outsider.GetAsync("/")).StatusCode == HttpStatusCode.Found, "Sign-in without a token offers a fresh form without authenticating");
            var failedLogin = await Post(outsider, "/login", "/login", Login("first.player@example.com", "incorrect"));
            Check(failedLogin.StatusCode == HttpStatusCode.OK && WebUtility.HtmlDecode(await failedLogin.Content.ReadAsStringAsync()).Contains("Couldn't sign in") && (await outsider.GetAsync("/")).StatusCode == HttpStatusCode.Found, "Wrong password shows an inline error without authenticating or HTTP 400");
            var staleLogin = await Form(outsider, "/login", Login("first.player@example.com", password));
            Check((await Post(outsider, "/login", "/login", Login("FIRST.PLAYER@EXAMPLE.COM", password))).StatusCode == HttpStatusCode.Found, "Email sign-in is case insensitive");
            Check((await outsider.PostAsync("/login", new FormUrlEncodedContent(staleLogin))).Headers.Location?.ToString() == "/", "A stale anonymous sign-in form redirects an already signed-in browser");
            var blockedLogout = await first.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string>()));
            Check((await blockedLogout.Content.ReadAsStringAsync()).Contains("Continue safely") && (await first.GetAsync("/")).StatusCode == HttpStatusCode.OK, "Logout without a token offers recovery and preserves the session");
            var staleAccount = await Form(first, "/account", new());
            var change = await Post(first, "/account", "/account/password", new() { ["currentPassword"] = password, ["password"] = newPassword, ["confirmPassword"] = newPassword });
            Check((await change.Content.ReadAsStringAsync()).Contains("Password changed"), "Account page changes the password");
            await Post(first, "/account", "/logout", new());
            Check((await first.GetAsync("/")).StatusCode == HttpStatusCode.Found, "Logout revokes the browser session");
            staleAccount["email"] = "first.player@example.com"; staleAccount["password"] = newPassword;
            var expired = await first.PostAsync("/login", new FormUrlEncodedContent(staleAccount));
            var expiredHtml = await expired.Content.ReadAsStringAsync();
            Check(expired.StatusCode == HttpStatusCode.OK && expiredHtml.Contains("session changed") && (await first.GetAsync("/")).StatusCode == HttpStatusCode.Found, "Token belonging to a signed-out user recovers safely without authenticating");
            var retryToken = WebUtility.HtmlDecode(Regex.Match(expiredHtml, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
            var retry = Login("first.player@example.com", newPassword); retry["__RequestVerificationToken"] = retryToken;
            Check((await first.PostAsync("/login", new FormUrlEncodedContent(retry))).StatusCode == HttpStatusCode.Found, "Fresh token on the recovery page successfully signs in");
            await Post(first, "/account", "/logout", new());
            await Post(first, "/login", "/login", Login("first.player@example.com", password));
            Check((await first.GetAsync("/")).StatusCode == HttpStatusCode.Found, "Old password no longer signs in");
            Check((await Post(first, "/login", "/login", Login("first.player@example.com", newPassword))).StatusCode == HttpStatusCode.Found, "New password signs in");
            using var lockoutClient = Client();
            for (int i = 0; i < 5; i++) await Post(lockoutClient, "/login", "/login", Login("second.player@example.com", "incorrect"));
            await Post(lockoutClient, "/login", "/login", Login("second.player@example.com", password));
            Check((await lockoutClient.GetAsync("/")).StatusCode == HttpStatusCode.Found, "Repeated failed attempts temporarily lock the account");
            if (browser) await Browser(host, cookieJars[first], async tab =>
            {
                await tab.Wait("document.body.innerText.includes('PRIVATE LEAGUE')");
                await tab.Screenshot("artifacts/refreshed-dashboard.png");
                await tab.Click("Try a practice draft");
                await tab.Wait("document.body.innerText.includes('Meet in the draft room')");
                await tab.Click("David · I'm ready"); await tab.Wait("document.body.innerText.includes('David · Ready')");
                await tab.Eval("(()=>{const b=Array.from(document.querySelectorAll('button')).find(b=>b.textContent.includes(\"Your rival · I'm ready\"));b.disabled=false;b.click();})()");
                await tab.Wait("document.body.innerText.includes('You can only act for your signed-in participant')");
                Check(true, "New Identity account cannot act for the rival even after tampering with a disabled button");
                await tab.Command("Page.navigate", new { url = new Uri(host.Address, "account").ToString() });
                await tab.Wait("document.body.innerText.includes('Change your password')");
                await tab.Screenshot("artifacts/account-settings.png");
            });
            await host.Restart();
            using var returning = Client();
            Check((await Post(returning, "/login", "/login", Login("first.player@example.com", newPassword))).StatusCode == HttpStatusCode.Found, "Email account and changed password survive server restart");
            Check((await first.GetAsync("/")).StatusCode == HttpStatusCode.OK, "Account session survives server restart using persisted protection keys");
            if (browser) await Browser(host, new CookieContainer(), async tab =>
            {
                await tab.Command("Emulation.setDeviceMetricsOverride", new { width = 390, height = 844, deviceScaleFactor = 1, mobile = true });
                await tab.Wait("document.querySelector('form[action=\"/login\"]') !== null");
                await tab.Eval($"document.querySelector('[name=email]').value = {JsonSerializer.Serialize("first.player@example.com")}; document.querySelector('[name=password]').value = {JsonSerializer.Serialize(newPassword)}; document.querySelector('form').requestSubmit();");
                await tab.Wait("document.body.innerText.includes('PRIVATE LEAGUE')");
                Check((await tab.Eval("location.pathname === '/'")).GetBoolean(), "Fresh mobile browser submits the actual sign-in form and reaches the interactive app");
                await tab.Command("Page.reload");
                await tab.Wait("document.querySelector('a.account-menu') !== null");
                await tab.Screenshot("artifacts/refreshed-mobile.png");
                Check(true, "Fresh mobile sign-in remains authenticated after a page reload");
            });
            using var forwarded = Client();
            forwarded.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
            var secureForm = await Form(forwarded, "/login", Login("first.player@example.com", newPassword));
            var secureCookies = cookieJars[forwarded].GetCookies(new UriBuilder(host.Address) { Scheme = "https" }.Uri);
            forwarded.DefaultRequestHeaders.Add("Cookie", string.Join("; ", secureCookies.Cast<Cookie>().Select(c => c.Name + "=" + c.Value)));
            var secureLogin = await forwarded.PostAsync("/login", new FormUrlEncodedContent(secureForm));
            Check(secureLogin.StatusCode == HttpStatusCode.Found && secureLogin.Headers.GetValues("Set-Cookie").Any(c => c.Contains(".SundayRivalry.Accounts=") && c.Contains("secure", StringComparison.OrdinalIgnoreCase)), "HTTPS forwarding supports valid sign-in and secure authentication cookies as used by Tailscale");
            Check(!(await returning.GetStringAsync("/account")).Contains("Create invitation link"), "Completed league no longer offers registration invitations");
            Console.WriteLine($"\n{checks} account checks passed.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
    }

    private static async Task Browser(TestHost host, CookieContainer jar, Func<Cdp, Task> action)
    {
        await using var chrome = new HeadlessChrome(Path.Combine(host.Directory, Guid.NewGuid().ToString("N"))); await chrome.Start();
        using var tab = await chrome.Tab(jar, host.Address);
        await tab.Command("Page.navigate", new { url = host.Address.ToString() });
        await action(tab);
    }
}

