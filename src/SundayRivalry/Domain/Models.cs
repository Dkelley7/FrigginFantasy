namespace SundayRivalry.Domain;

public class AppState
{
    public string[] Names { get; set; } = ["David", "Your rival"];
    public List<NflGame> Games { get; set; } = [];
    public List<Player> Players { get; set; } = [];
    public List<StatLine> Stats { get; set; } = [];
    public List<Competition> Competitions { get; set; } = [];
    public List<ProjectionSet> Projections { get; set; } = [];
    public List<PlayerInjury> Injuries { get; set; } = [];
    public EspnProjectionSettings Espn { get; set; } = new();
    public List<LiveBoxScore> LiveScores { get; set; } = [];
    public Dictionary<string, string> Tank01GameIds { get; set; } = [];
    public bool LivePollingPaused { get; set; }
    public string LiveStatus { get; set; } = "Tank01 is not configured. Historical statistics still import normally.";
    public DateTimeOffset? LastSync { get; set; }
    public string SyncMessage { get; set; } = "Ready to import NFL data. Try the practice room while your first sync runs.";
}

public class EspnProjectionSettings
{
    public DateTimeOffset? InjuriesUpdatedAt { get; set; }
    public long LeagueId { get; set; }
    public int Season { get; set; }
    public int Week { get; set; }
    public DateTimeOffset? LastAttempt { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public string Status { get; set; } = "Connect a publicly viewable ESPN league to import weekly projections.";
    public List<string> Issues { get; set; } = [];
}

public class PlayerInjury
{
    public string PlayerId { get; set; } = "";
    public string Designation { get; set; } = "";
    public string Source { get; set; } = "ESPN";
    public DateTimeOffset UpdatedAt { get; set; }
}

public class NflGame
{
    public string Id { get; set; } = "";
    public int Season { get; set; }
    public int Week { get; set; }
    public string Away { get; set; } = "";
    public string Home { get; set; } = "";
    public DateTimeOffset Kickoff { get; set; }
    public string Day { get; set; } = "";
    public string Stadium { get; set; } = "";
    public int? AwayScore { get; set; }
    public int? HomeScore { get; set; }
}

public class Player
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Position { get; set; } = "";
    public string Team { get; set; } = "";
    public string Status { get; set; } = "";
    public string Photo { get; set; } = "";
}

public class StatLine
{
    public string PlayerId { get; set; } = "";
    public int Season { get; set; }
    public int Week { get; set; }
    public string Team { get; set; } = "";
    public double PassYards { get; set; }
    public double PassTd { get; set; }
    public double Interceptions { get; set; }
    public double RushYards { get; set; }
    public double RushTd { get; set; }
    public double Receptions { get; set; }
    public double RecYards { get; set; }
    public double RecTd { get; set; }
    public double FumblesLost { get; set; }
    public double TwoPoint { get; set; }
    public double ReturnTd { get; set; }
    public double ExtraPoints { get; set; }
    public double FgShort { get; set; }
    public double Fg40 { get; set; }
    public double Fg50 { get; set; }
    public double Sacks { get; set; }
    public double Takeaways { get; set; }
    public double DefenseTd { get; set; }
    public double Safeties { get; set; }
    public double Blocks { get; set; }
    public int PointsAllowed { get; set; }
    public bool Defense { get; set; }
    public bool DidNotRecordStats { get; set; }
    public bool IsLive { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public List<string> CoverageIssues { get; set; } = [];
    public double Points(double ppr) => Defense
        ? Sacks + Takeaways * 2 + DefenseTd * 6 + Safeties * 2 + Blocks * 2 + DefensePoints(PointsAllowed)
        : PassYards * .04 + PassTd * 4 - Interceptions * 2 + RushYards * .1 + RushTd * 6
          + Receptions * ppr + RecYards * .1 + RecTd * 6 - FumblesLost * 2 + TwoPoint * 2
          + ExtraPoints + FgShort * 3 + Fg40 * 4 + Fg50 * 5 + ReturnTd * 6;
    public static int DefensePoints(int points) => points switch { 0 => 10, <= 6 => 7, <= 13 => 4, <= 20 => 1, <= 27 => 0, <= 34 => -1, _ => -4 };
}

public class LiveBoxScore
{
    public string GameId { get; set; } = "";
    public string ProviderGameId { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public bool Final { get; set; }
    public List<StatLine> Stats { get; set; } = [];
    public List<string> Issues { get; set; } = [];
}

public class Candidate
{
    public Player Player { get; set; } = new();
    public string GameId { get; set; } = "";
    public string Opponent { get; set; } = "";
    public DateTimeOffset Kickoff { get; set; }
    public double Projection { get; set; }
    public double RecentAverage { get; set; }
    public int SampleSize { get; set; }
    public int Salary { get; set; }
    public string ProjectionSource { get; set; } = "";
    public DateTimeOffset? ProjectionImportedAt { get; set; }
    public string EstimateLabel => ProjectionSource.Length > 0 ? ProjectionSource : SampleSize > 0 ? $"{SampleSize}-game avg" : "Position baseline";
}

public class ProjectionSet
{
    public int Season { get; set; }
    public int Week { get; set; }
    public double Ppr { get; set; }
    public string Source { get; set; } = "FanDuel Research";
    public DateTimeOffset ImportedAt { get; set; }
    public List<PlayerProjection> Players { get; set; } = [];
}

public class PlayerProjection
{
    public string PlayerId { get; set; } = "";
    public double Points { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
}

public class Pick
{
    public string PlayerId { get; set; } = "";
    public string Slot { get; set; } = "";
    public int Owner { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public class Competition
{
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeProvider Clock { get; set; } = TimeProvider.System;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public int Season { get; set; }
    public int Week { get; set; }
    public string Format { get; set; } = "Snake";
    public double Ppr { get; set; } = 1;
    public int Budget { get; set; } = 200;
    public bool Demo { get; set; }
    public string[] Names { get; set; } = [];
    public bool[] Ready { get; set; } = [false, false];
    public List<NflGame> Games { get; set; } = [];
    public List<Candidate> Pool { get; set; } = [];
    public List<Pick> Picks { get; set; } = [];
    public int FirstOwner { get; set; }
    public int? UndoRequestedBy { get; set; }
    public int Revision { get; set; }
    public string RulesVersion { get; set; } = "1";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LockAt => Games.Min(x => x.Kickoff);
    public bool Locked => Clock.GetUtcNow() >= LockAt;
    public bool DraftComplete => Picks.Count == 18;
    public int Turn => Rules.SnakeOwner(Picks.Count, FirstOwner);
    public bool Started => Ready.All(x => x);
}

public static class Rules
{
    public static readonly string[] Slots = ["QB", "RB1", "RB2", "WR1", "WR2", "TE", "FLEX", "K", "D/ST"];
    public static readonly string[] Positions = ["QB", "RB", "WR", "TE", "K", "D/ST"];
    public static bool Fits(string slot, string position) => slot switch
    {
        "RB1" or "RB2" => position == "RB",
        "WR1" or "WR2" => position == "WR",
        "FLEX" => position is "RB" or "WR" or "TE",
        _ => slot == position
    };
    public static int SnakeOwner(int pick, int first) => (pick / 2 % 2 == 0 ? pick % 2 : 1 - pick % 2) ^ first;
    public static string? OpenSlot(Competition c, int owner, string position) => Slots.FirstOrDefault(s => Fits(s, position) && !c.Picks.Any(p => p.Owner == owner && p.Slot == s));
    public static string ScoringName(double ppr) => ppr == 1 ? "Full PPR" : ppr == .5 ? "Half PPR" : "Standard";
    public static bool CanViewRoster(Competition c, int? viewer, int owner, DateTimeOffset? now = null) =>
        c.Format != "Salary" || viewer == owner || (now ?? c.Clock.GetUtcNow()) >= c.LockAt;
}
