using SundayRivalry.Domain;

namespace SundayRivalry.Services;

public static class DemoData
{
    public static List<NflGame> Games(DateTimeOffset now) => new[] { ("KC", "BUF"), ("PHI", "BAL"), ("DET", "SF"), ("CIN", "DAL") }
        .Select((t, i) => new NflGame { Id = $"practice-{i}", Season = now.Year, Week = 1, Away = t.Item1, Home = t.Item2, Kickoff = now.AddDays(7).AddHours(i), Day = "Practice", Stadium = "Practice slate · fictional matchup" }).ToList();

    public static List<Player> Players()
    {
        // Intentionally fictional athletes: practice values never masquerade as NFL data.
        var names = new[] { "Alex", "Jordan", "Casey", "Riley", "Morgan", "Taylor", "Sam", "Cameron" };
        var teams = new[] { "KC", "BUF", "PHI", "BAL", "DET", "SF", "CIN", "DAL" };
        var players = new List<Player>();
        for (int t = 0; t < teams.Length; t++)
        {
            foreach (var (position, suffix) in new[] { ("QB", "Carter"), ("RB", "Brooks"), ("RB", "Reed"), ("WR", "Hayes"), ("WR", "Parker"), ("TE", "Bennett"), ("K", "Ellis"), ("D/ST", "Defense") })
                players.Add(new() { Id = $"demo-{teams[t]}-{suffix}", Name = position == "D/ST" ? $"{teams[t]} Defense" : $"{names[t]} {suffix}", Team = teams[t], Position = position, Status = "Practice" });
        }
        return players;
    }
}
