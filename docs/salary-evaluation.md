# Salary system evaluation - October 16, 2026

Recommendation: retain the 200-credit default and improve price generation and projection quality. The denomination is suitable for this app. A 200-credit budget is not, by itself, evidence that prices are balanced.

## External comparisons

| Product | Published budget | Relevance |
| --- | --- | --- |
| Yahoo Daily Fantasy | 200 | Direct evidence that a weekly fixed-price game can use this denomination. [Yahoo lineup instructions](https://help.yahoo.com/kb/fantasy-football/set-lineup-sln26039.html) |
| DraftKings Classic | 50,000 | Fixed-price, cap-constrained roster construction. Its published overview describes performance-based salaries, without disclosing a reproducible pricing formula. [DraftKings instructions](https://www.draftkings.com/help/how-to-play/us) |
| FanDuel traditional NFL DFS | 60,000 | Fixed-price format; published roster uses three WR and no kicker. Its half-PPR scoring also differs from this app's default. [FanDuel guide](https://www.fanduel.com/research/fantasy-football-strategy) |
| Sleeper auction | Customizable | Players bid against each other for exclusive players. This differs from our independent, shared-player salary lineups, so auction values should not be copied into this app. [Sleeper auction rules](https://support.sleeper.com/en/articles/4056268-do-you-support-auction-drafts) |

These sources establish budgets and formats, not the providers' private pricing algorithms. There is no claim that our proposed calibration duplicates their algorithms. Single-game provider contests may use different roster rules; our single-game slate retains the app's starting nine.

## Current implementation

`GameService.BuildPool` first selects the latest projection set matching season, week and PPR. A matched player receives those points. Otherwise, it takes an unweighted average of up to five prior stat lines under the selected scoring. Current-week and future-week outcomes are excluded. A player without history receives a position baseline: QB 13, RB/WR 5, TE 4, K/DST 6.

At 200 credits:

```
salary = max(1, round(3 + 1.1 * estimatedPoints))
```

For other budgets, the unrounded price is multiplied by `budget / 200`. Thus increasing or decreasing the budget mostly changes the units, not purchasing power. Integer rounding causes small exceptions. At 200 credits, 10 estimated points costs 14, 20 costs 25, and 30 costs 36.

For a complete nine-player lineup, ignoring rounding and the minimum-price floor:

```
lineup cost = 27 + 1.1 * total estimated points
200 credits buys approximately 157.3 estimated points
```

Consequences:

- Every player follows essentially the same points-to-price relationship. Prices contain little independent information about positional supply, roles or market value. The fixed overhead makes higher projections more efficient per credit.
- Smaller slates can make the cap irrelevant. Wider slates and full PPR can make it much tighter.
- No-history estimates do not distinguish a new starting player from a backup. Historical averages do not explicitly adjust for current injuries, depth-chart changes, opponent, weather or recent usage. Averages use available stat lines rather than a complete appearance history, so missing/zero-stat appearances can bias them.
- Current import validation asks the user to confirm scoring. A provider's half-PPR points cannot be converted to our full-PPR points accurately without projected receptions, and other scoring differences also matter.
- Existing competition pools correctly freeze projections and salaries at creation. Future pricing changes should preserve those snapshots.

## Read-only audit of cached data

The audit opened `rivalry.db` in SQLite read-only mode. At audit time it contained 677 players, 24,795 historical stat lines and **zero imported projection sets**. The last sync was October 16, 2026 at 16:06 UTC. Professional projections are supported by the importer but are not currently present in this database.

An exact positional optimizer assigns each player at most once, including FLEX. It calculates the cheapest legal roster and the highest projected legal roster without imposing the budget. Among equally projected rosters, it prefers the cheaper one. It uses the actual app's nine slots and salary formula.

| Cached slate | Standard: best roster cost | Half PPR: best roster cost | Full PPR: best roster cost |
| --- | ---: | ---: | ---: |
| Week 6 Sunday, 12 games | 204 | 218 | 236 |
| Week 6 first upcoming single game | 126 | 139 | 150 |
| Week 7 Sunday | 211 | 229 | 249 |
| Week 7 first upcoming single game | 125 | 136 | 145 |
| Week 8 Sunday | 212 | 229 | 249 |
| Week 8 first upcoming single game | 140 | 153 | 166 |

The Week 6 Sunday full-PPR maximum is 189.5 estimated points at a price of 236 credits. Standard's maximum is 160 points at 204 credits. In contrast, all nine single-game/scoring examples above can afford the maximum projected roster with substantial credits left. The 200 cap therefore imposes very different decisions across supported setups.

These are measurements of cached estimates, not forecasts of actual outcomes. Future-week estimates use the history available at audit time, not future data. Later syncs or projection imports can change the measurements. The diagnostic evaluates highest expected points, not outcome variance or every possible strategy.

The complete 27-scenario output is `artifacts/salary-audit.json`. Re-run after a test build from the repository root:

```powershell
dotnet artifacts/check-build/bin/SundayRivalry.Checks/debug/SundayRivalry.Checks.dll --salary-audit src/SundayRivalry/App_Data/rivalry.db
```

This reads the named database and writes only a local audit report. It never initializes the app against that database, changes competitions, or calls external feeds. The optimizer has known-result tests in the verification suite.

## Proposed replacement

1. Keep the default cap at 200 credits. Treat this as the display denomination; if a difficulty setting is desired, expose it separately from the unit scale.
2. Improve estimates first. Prefer fresh, matching pregame projections; where stat-category projections are available, calculate points using our exact rules. Make historical fallbacks explicit and use recency weighting plus a position prior for small samples. Select weights through historical, pre-kickoff backtesting rather than claiming they are accurate by assumption.
3. Price using role-aware position tiers and the selected slate. Compare plausible starters rather than computing percentiles over every low-usage backup. Without reliable role/depth-chart information, do not silently exclude players or advertise a starter-aware model. Imported provider salaries could be an optional input, with source and scoring/roster differences recorded.
4. Calibrate total pricing pressure for our roster. As an initial design target, make the best unconstrained projected roster cost around 240-260 credits while ensuring several competitive alternatives fit within 200 and the cheapest legal roster remains affordable. This 120-130% range is a proposed tuning target, not a published industry standard or validated optimum. Tiny slates with insufficient projection diversity need special handling rather than merely inflating all prices.
5. Check variation across standard, half PPR, full PPR and every slate size. Verify value options, affordable completions after picks, and changes in which rosters fit. Keep the same-player-on-both-teams rule; pressure is evaluated per roster, not against a two-team auction market.
6. Store a salary-model version and projection provenance in each new competition. Freeze all prices once the competition is created. Evaluate a historical sample before replacing the current formula.

A useful acceptance suite would check cap pressure on realistic wide and small slates, feasible rosters, positional price ranges, budget rounding, missing-data fallbacks, monotonicity within comparable tiers, scoring presets, deterministic generation, and preservation of old competitions. A date-night release should not silently change the rules of an already created competition.

No production pricing or existing competitions were changed as part of this evaluation.

