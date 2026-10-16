using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

static class BrowserChecks
{
    public static async Task Run()
    {
        var baseUrl = Environment.GetEnvironmentVariable("SUNDAY_RIVALRY_BROWSER_URL") ?? throw new InvalidOperationException("Legacy browser checks require an explicitly isolated SUNDAY_RIVALRY_BROWSER_URL. Use scripts/check.ps1 for the automatic safe suite.");
        if (new Uri(baseUrl).Port == 5180) throw new InvalidOperationException("Browser checks must not target the normal app on port 5180.");
        Directory.CreateDirectory("artifacts");
        using var http = new HttpClient();
        var targets = JsonDocument.Parse(await http.GetStringAsync("http://localhost:9223/json/list"));
        var target = targets.RootElement.EnumerateArray().First(t => t.GetProperty("type").GetString() == "page");
        using var cdp = new Cdp();
        await cdp.Connect(target.GetProperty("webSocketDebuggerUrl").GetString()!);
        await cdp.Command("Runtime.enable"); await cdp.Command("Page.enable");
        await cdp.Command("Page.navigate", new { url = baseUrl });
        await Task.Delay(1200);
        await cdp.Wait("document.body.innerText.includes('A little friendly rivalry')");
        await cdp.Screenshot("artifacts/desktop-dashboard.png");
        await cdp.Click("Try a practice draft");
        await cdp.Wait("document.body.innerText.includes('Meet in the draft room')");
        await cdp.Click("David · I'm ready"); await cdp.Click("Your rival · I'm ready");
        await cdp.Wait("document.body.innerText.includes(\"David, you're up\")");
        await cdp.Screenshot("artifacts/desktop-draft.png");
        await cdp.Eval("document.querySelector('.pick-button:not([disabled])').click()");
        await cdp.Wait("document.querySelector('[role=dialog]') !== null");
        await cdp.Click("Make the pick");
        await cdp.Wait("document.body.innerText.includes(\"Your rival, you're up\")");
        await cdp.Click("David: Request undo"); await cdp.Click("Your rival: Approve undo");
        await cdp.Wait("document.body.innerText.includes(\"David, you're up\")");
        Console.WriteLine("PASS Browser snake lobby, ready, pick, turn update, and mutual undo");
        // The same room in a separate tab must receive picks without a reload.
        var response = await http.PutAsync("http://localhost:9223/json/new?" + baseUrl, null);
        var secondTarget = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        using var other = new Cdp(); await other.Connect(secondTarget.GetProperty("webSocketDebuggerUrl").GetString()!);
        await other.Command("Runtime.enable"); await Task.Delay(1200);
        await other.Click("The practice draft");
        await other.Wait("document.body.innerText.includes(\"David, you're up\")");
        await cdp.Eval("document.querySelector('.pick-button:not([disabled])').click()");
        await cdp.Wait("document.querySelector('[role=dialog]') !== null"); await cdp.Click("Make the pick");
        await other.Wait("document.body.innerText.includes(\"Your rival, you're up\")");
        Console.WriteLine("PASS Browser second client receives draft updates");
        await cdp.Click("Your competitions"); await cdp.Click("Try salary cap");
        await cdp.Wait("document.body.innerText.includes('The budget playbook')");
        await cdp.Click("David · I'm ready"); await cdp.Click("Your rival · I'm ready");
        await cdp.Eval("document.querySelector('.pick-button:not([disabled])').click()");
        await cdp.Wait("document.querySelector('[role=dialog]') !== null"); await cdp.Click("Add to lineup");
        await cdp.Wait("document.body.innerText.includes('credits left')");
        await cdp.Screenshot("artifacts/desktop-salary.png");
        await cdp.Click("Matchup & points");
        await cdp.Wait("document.querySelector('.matchup-score') !== null");
        await cdp.Click("Pick history");
        await cdp.Wait("document.body.innerText.includes('1 selections')");
        Console.WriteLine("PASS Browser salary purchase, matchup, and pick history");
        await cdp.Click("New competition");
        await cdp.Wait("document.body.innerText.includes('Choose your slate')");
        await cdp.Screenshot("artifacts/desktop-setup.png");
        await cdp.Click("Create competition");
        await cdp.Wait("document.body.innerText.includes('Meet in the draft room')");
        await cdp.Click("David · I'm ready"); await cdp.Click("Your rival · I'm ready");
        await cdp.Wait("document.querySelector('.pick-button:not([disabled])') !== null");
        await cdp.Screenshot("artifacts/desktop-real-draft.png");
        await cdp.Eval("document.querySelector('.pick-button:not([disabled])').click()");
        await cdp.Wait("document.body.innerText.includes('Recent appearances')");
        await cdp.Screenshot("artifacts/player-stats.png");
        await cdp.Eval("document.querySelector('.modal-close').click()");
        Console.WriteLine("PASS Browser creates real NFL slate and displays historical player stats");
        await cdp.Click("Data & settings");
        await cdp.Wait("document.body.innerText.includes('The scoring playbook')");
        await cdp.Screenshot("artifacts/desktop-settings.png");
        var fixturePlayer = SundayRivalry.Services.DataSync.ParsePlayers(await File.ReadAllTextAsync(".tools/research/rosters.csv")).First(p => p.Position == "QB");
        var importFile = Path.GetFullPath("artifacts/synthetic-projection-upload.csv");
        await File.WriteAllTextAsync(importFile, $"Player,Team,Position,Projection\n\"{fixturePlayer.Name.Replace("\"", "\"\"")}\",{fixturePlayer.Team},{fixturePlayer.Position},23.45\nSynthetic Unmatched Player,KC,WR,11.2\n");
        var document = await cdp.Command("DOM.getDocument");
        var fileInput = await cdp.Command("DOM.querySelector", new { nodeId = document.GetProperty("root").GetProperty("nodeId").GetInt32(), selector = ".projection-import input[type=file]" });
        await cdp.Command("DOM.setFileInputFiles", new { nodeId = fileInput.GetProperty("nodeId").GetInt32(), files = new[] { importFile } });
        await cdp.Wait("document.body.innerText.includes('2 rows loaded')");
        await cdp.Eval("document.querySelector('.projection-confirm input').click()");
        await cdp.Click("Preview player matches");
        await cdp.Wait("document.body.innerText.includes('1 matched') && document.body.innerText.includes('1 skipped')");
        await cdp.Screenshot("artifacts/projection-import-preview.png");
        await cdp.Click("Save 1 matched projections");
        await cdp.Wait("document.body.innerText.includes('Saved projections for')");
        await cdp.Command("Emulation.setDeviceMetricsOverride", new { width = 390, height = 844, deviceScaleFactor = 1, mobile = true });
        if (!(await cdp.Eval("document.documentElement.scrollWidth <= window.innerWidth")).GetBoolean()) throw new Exception("Projection importer overflows a phone screen");
        await cdp.Screenshot("artifacts/projection-import-mobile.png");
        await cdp.Command("Emulation.clearDeviceMetricsOverride");
        Console.WriteLine("PASS Browser projection CSV upload, column detection, match preview, save, and mobile layout (synthetic fixture)");
        await cdp.Click("Overview");
        await cdp.Command("Emulation.setDeviceMetricsOverride", new { width = 390, height = 844, deviceScaleFactor = 1, mobile = true });
        await Task.Delay(300);
        await cdp.Screenshot("artifacts/mobile-dashboard.png");
        if ((await cdp.Eval("document.documentElement.scrollWidth <= window.innerWidth")).GetBoolean() != true) throw new Exception("Mobile dashboard overflows horizontally");
        await cdp.Click("The budget playbook");
        await cdp.Wait("document.querySelector('.player-table') !== null");
        await cdp.Screenshot("artifacts/mobile-draft.png");
        if (!(await cdp.Eval("document.documentElement.scrollWidth <= window.innerWidth")).GetBoolean()) throw new Exception("Mobile draft overflows horizontally");
        Console.WriteLine("PASS Browser mobile dashboard and draft have no horizontal page overflow");
        await cdp.Command("Emulation.clearDeviceMetricsOverride");
        await cdp.Click("Overview");
        if (cdp.Errors.Count > 0 || other.Errors.Count > 0) throw new Exception("Browser errors: " + string.Join("\n", cdp.Errors.Concat(other.Errors)));
        Console.WriteLine("PASS Browser no JavaScript exceptions. Screenshots saved in artifacts/.");
    }
}

sealed class Cdp : IDisposable
{
    readonly ClientWebSocket socket = new(); int nextId;
    public List<string> Errors = [];
    public async Task Connect(string uri) => await socket.ConnectAsync(new Uri(uri), CancellationToken.None);
    public async Task<JsonElement> Command(string method, object? parameters = null)
    {
        var id = ++nextId;
        await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, method, @params = parameters ?? new { } })), WebSocketMessageType.Text, true, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var buffer = new byte[65536];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult part;
            do { part = await socket.ReceiveAsync(buffer, timeout.Token); message.Write(buffer, 0, part.Count); } while (!part.EndOfMessage);
            using var json = JsonDocument.Parse(message.ToArray());
            var root = json.RootElement;
            if (root.TryGetProperty("method", out var eventName) && eventName.GetString() == "Runtime.exceptionThrown") Errors.Add(root.ToString());
            if (root.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
            {
                if (root.TryGetProperty("error", out var error)) throw new Exception(error.ToString());
                return root.GetProperty("result").Clone();
            }
        }
    }
    public async Task<JsonElement> Eval(string script)
    {
        var result = await Command("Runtime.evaluate", new { expression = script, returnByValue = true, awaitPromise = true });
        if (result.TryGetProperty("exceptionDetails", out var exception)) throw new Exception(exception.ToString());
        return result.GetProperty("result").TryGetProperty("value", out var value) ? value.Clone() : default;
    }
    public async Task Wait(string condition)
    {
        for (int i = 0; i < 100; i++) { var result = await Eval(condition); if (result.ValueKind == JsonValueKind.True) return; await Task.Delay(100); }
        throw new Exception("Browser wait timed out: " + condition + "\n" + (await Eval("document.body.innerText")).GetString());
    }
    public async Task Click(string label)
    {
        var literal = JsonSerializer.Serialize(label);
        var query = $"Array.from(document.querySelectorAll('button')).find(b => b.textContent.includes({literal}) && !b.disabled)";
        await Wait(query + " !== undefined"); await Eval(query + ".click()"); await Task.Delay(150);
    }
    // Only use for idempotent navigation. Prerendered HTML may arrive before
    // Blazor has attached handlers, especially on a clean or slower CI runner.
    public async Task Navigate(string label, string arrived)
    {
        var literal = JsonSerializer.Serialize(label);
        await Wait($"(()=>{{if ({arrived}) return true; const button=Array.from(document.querySelectorAll('button')).find(b=>b.textContent.includes({literal})&&!b.disabled);button?.click();return false;}})()");
    }
    public async Task Screenshot(string path)
    {
        await Command("Page.bringToFront");
        await Task.Delay(150);
        var result = await Command("Page.captureScreenshot", new { format = "png", captureBeyondViewport = true });
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(result.GetProperty("data").GetString()!));
    }
    public void Dispose() => socket.Dispose();
}
