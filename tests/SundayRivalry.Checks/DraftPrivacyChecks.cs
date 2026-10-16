using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SundayRivalry.Components.Shared;
using SundayRivalry.Domain;
using SundayRivalry.Services;

static class DraftPrivacyChecks
{
    public static async Task Run(GameService store)
    {
        var checks = 0;
        void Check(bool pass, string label)
        {
            if (!pass) throw new Exception("FAILED " + label);
            Console.WriteLine("PASS " + label); checks++;
        }
        var kickoff = DateTimeOffset.UtcNow.AddHours(1);
        var c = new Competition
        {
            Format = "Salary", Names = ["First player", "Second player"], Ready = [true, true], Demo = true,
            Games = [new() { Kickoff = kickoff }],
            Pool = [
                new() { Player = new() { Id = "one", Name = "First Secret", Position = "QB" }, Projection = 21.3, Salary = 26 },
                new() { Player = new() { Id = "two", Name = "Second Secret", Position = "QB" }, Projection = 19.7, Salary = 25 },
                new() { Player = new() { Id = "costly", Name = "Expensive Runner", Position = "RB" }, Projection = 30, Salary = 180 }
            ],
            Picks = [new() { Owner = 0, PlayerId = "one", Slot = "QB" }, new() { Owner = 1, PlayerId = "two", Slot = "QB" }]
        };
        Check(!Rules.CanViewRoster(c, 0, 1, kickoff.AddTicks(-1)) && !Rules.CanViewRoster(c, 1, 0, kickoff.AddTicks(-1)), "Both salary rosters remain private immediately before kickoff");
        Check(Rules.CanViewRoster(c, 0, 1, kickoff) && Rules.CanViewRoster(c, 1, 0, kickoff), "Both salary rosters reveal exactly at the first included kickoff");
        Check(!Rules.CanViewRoster(c, null, 1, kickoff.AddTicks(-1)), "Unknown viewers cannot see private salary rosters");

        async Task<string> Render(int viewer, string view)
        {
            var services = new ServiceCollection();
            services.AddLogging(); services.AddSingleton(store);
            services.AddSingleton<IComponentActivator>(new ViewActivator(view));
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await renderer.RenderComponentAsync<DraftRoom>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(DraftRoom.C)] = c, [nameof(DraftRoom.State)] = new AppState(), [nameof(DraftRoom.SignedInOwner)] = viewer
                }));
                return output.ToHtmlString();
            });
        }

        for (var viewer = 0; viewer < 2; viewer++)
        {
            var ownName = c.Pool[viewer].Player.Name;
            var opponentName = c.Pool[1 - viewer].Player.Name;
            var draft = await Render(viewer, "draft");
            var hidden = Regex.Match(draft, $"<section class=\"panel roster roster-private\" data-owner=\"{1 - viewer}\">.*?</section>", RegexOptions.Singleline).Value;
            Check(hidden.Contains("PRIVATE UNTIL KICKOFF") && !hidden.Contains(opponentName) && !hidden.Contains("credits left") && !hidden.Contains("roster-total"), $"Player {viewer + 1}'s draft does not emit opponent roster, budget, or totals");
            Check(Regex.Matches(draft, "class=\"budget\"").Count == 1 && draft.Contains("build your lineup at your own pace"), $"Player {viewer + 1} builds independently with only their own budget visible");
            Check(draft.Contains(viewer == 0 ? "21.75" : "21.88") && draft.Contains("8 positions left") && !hidden.Contains("Avg. per open position"), $"Player {viewer + 1} sees only their own average credits per open position");
            var expensiveRow = Regex.Match(draft, "<tr[^>]*class=\"player-unaffordable\"[^>]*>.*?</tr>", RegexOptions.Singleline).Value;
            Check(expensiveRow.Contains("Expensive Runner") && expensiveRow.Contains("Over budget") && expensiveRow.Contains("disabled") && expensiveRow.Contains("aria-disabled=\"true\""), $"Player {viewer + 1} can see an unaffordable player in a muted row with selection disabled");
            var history = await Render(viewer, "log");
            Check(history.Contains(ownName) && !history.Contains(opponentName) && history.Contains("1 selections"), $"Player {viewer + 1}'s history contains only their own salary picks");
            var results = await Render(viewer, "results");
            Check(results.Contains(ownName) && !results.Contains(opponentName) && results.Contains("Hidden") && results.Contains("Reveals at kickoff"), $"Player {viewer + 1}'s matchup hides opponent picks and projected score");
        }

        c.Games[0].Kickoff = DateTimeOffset.UtcNow.AddSeconds(-1);
        foreach (var view in new[] { "draft", "log", "results" })
        {
            var html = await Render(0, view);
            Check(html.Contains("First Secret") && html.Contains("Second Secret") && !html.Contains("roster-private"), $"Salary {view} reveals both lineups after kickoff");
        }
        c.Format = "Snake"; c.Games[0].Kickoff = kickoff;
        foreach (var view in new[] { "draft", "log", "results" })
        {
            var html = await Render(0, view);
            Check(html.Contains("First Secret") && html.Contains("Second Secret") && !html.Contains("roster-private"), $"Snake {view} keeps opponent picks visible before kickoff");
        }
        Console.WriteLine($"\n{checks} draft privacy checks passed.");
    }

    // Start the real component on each tab without exposing a testing-only UI parameter.
    private sealed class ViewActivator(string view) : IComponentActivator
    {
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is DraftRoom)
                typeof(DraftRoom).GetField("view", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, view);
            return component;
        }
    }
}
