# Weekly Fantasy Football — implementation plan

Status: initial implementation completed. See README.md and verification.md for
launch instructions, verified behavior, and current limitations.

## Purpose

A private, just-for-fun NFL app for two people. Every competition stands alone,
with fresh rosters and either a snake draft or a fixed-price salary-cap format.
There are no payments, wagers, or prizes managed by the app.

## Agreed requirements

- Start as a localhost .NET application on the desktop. Use responsive layouts
  and support optional access from browsers on the home network.
- Preserve a path to inexpensive hosting with two private accounts.
- Choose season, NFL week, and game slates during competition setup. Support
  Thursday, Sunday, Monday, combinations, and Saturday or other scheduled days
  when applicable. Show the exact included games before confirmation.
- Both participants join and mark ready before drafting or salary selection.
- Snake format: exclusive players, alternating turns with reversed order each
  round; alternate the starting participant between competitions. No pick timer.
  Undo requires agreement from both participants.
- Salary format: fixed player prices, one budget per team, and the same player
  may appear on both teams. A player may appear only once within a team.
- Roster: 1 QB, 2 RB, 2 WR, 1 TE, 1 FLEX (RB/WR/TE), 1 K, 1 team D/ST.
  No bench. Nine selections per participant.
- Full PPR by default; offer half PPR and standard scoring in setup.
- Lock all rosters at the scheduled kickoff of the first included game.
  Enforce this on the server, including after an application restart.
- Show weekly competitions, historical results, player statistics, projections,
  salary budgets, draft history, and player-level scoring breakdowns.
- Prefer free data. Live scoring is desirable but optional; accept pregame
  estimates and delayed statistics when a suitable free feed is unavailable.

## Proposed implementation defaults

These are implementation choices rather than additional user requirements.

- ASP.NET Core / Blazor on .NET 10 LTS; EF Core with SQLite.
- One application process and database, with server-side draft validation and
  shared updates between connected browsers. Reconnects reload saved state.
- In-app email/password accounts using ASP.NET Core Identity, enabled by default.
  The first participant registers and invites the second by an email-bound link;
  registration closes at two accounts. A shared-screen mode remains available
  only as an explicit development setting.
- Configurable salary cap, initially 200 fantasy credits. Generate prices from
  pregame estimates and eligible player supply; ensure legal rosters are affordable.
- Four points per passing touchdown; standard yardage, turnover, kicker, and
  D/ST rules documented and visible in setup. Scoring presets differ by points
  per reception. Save a copy of the full rules with every competition.
- Draft setup cannot include games that have already started. An unfinished
  roster at lock is flagged incomplete; empty positions score zero, with no
  automatic picks or purchases.
- Salary lineups can be edited until lock. Snake picks can be undone only with
  mutual agreement before lock. Budgets and roster eligibility are always validated.
- Exact ties remain ties. History includes a head-to-head record.
- Store timestamps in UTC and display kickoff times in the configured local zone.
- Store draft-time projections, prices, selected games, and scoring rules as
  competition snapshots. Preserve source statistics and track later corrections.

## Data approach

- Bulk-import nflverse players, teams, rosters, schedules, and historical player
  and team statistics into SQLite. Consume downloadable data directly from .NET;
  R and Python are not runtime requirements.
- Keep stable internal IDs with provider ID mappings. Do not join by name alone.
- Refresh player/roster metadata daily while the app is running; refresh schedules
  before competition creation and readiness. Provide a manual sync action.
- Generate clearly labeled estimates from recent historical performance initially.
  Do not present generated values as professional projections. Make provider
  projections replaceable without changing competition logic.
- Import available results periodically while relevant competitions are active.
  Polling does not imply that the upstream statistics are live. Show source,
  last successful sync, and delayed/stale status. Recheck completed games for
  corrections before finalizing results.
- Cache image assets only where source terms permit; provide fallback avatars.
- Do not depend on undocumented Sleeper or ESPN live endpoints.
- Investigate MySportsFeeds as an optional authenticated feed if free personal-use
  access is obtained. Do not assume live access or contact the provider on the
  user's behalf. No paid services are required for the baseline.
- A desktop app only synchronizes while its process is running; catch up on start.

## Screens

1. Dashboard: current competition, week selector, NFL games, past results, record.
2. Setup/lobby: game slate selection, format, scoring, budget, participants, ready.
3. Draft room: player search/filters, opponent/kickoff, recent performance,
   estimates, salary, both rosters, turn status or remaining budgets, draft log.
4. Matchup/results: roster comparisons, stat-to-point breakdown, freshness,
   provisional/final status, winner and historical snapshots.
5. Data/settings: sync controls, import status, scoring reference, account settings.

## Implementation sequence

1. Install or provision the .NET 10 SDK; scaffold the app and database migrations.
2. Implement competition rules, roster validation, scoring, and reliable lock checks.
3. Add bulk data imports, generated estimates/prices, and repeatable demo data.
4. Build the dashboard, setup, snake draft, and salary lineup workflows.
5. Add results synchronization, scoring breakdowns, history, and private access.
6. Validate shared-screen play, two-browser synchronization, reconnects, and mobile
   layouts. Document localhost launch and optional home-network setup.

## Verification

Meaningful automated checks cover snake order and exclusive picks, roster/FLEX
eligibility, salary arithmetic and affordability, cross-team duplicates in salary
mode, server-side locking, mutual undo, scoring presets including K/DST, import
idempotency, and result corrections. Exercise both complete competition workflows,
restart persistence, and competing requests for the same snake pick.

## Research references

- .NET support: https://dotnet.microsoft.com/en-us/platform/support/policy
- nflverse availability: https://nflreadr.nflverse.com/articles/nflverse_data_schedule.html
- nflverse players: https://nflreadr.nflverse.com/reference/load_players.html
- nflverse datasets: https://github.com/nflverse/nflverse-data
- Sleeper documented API: https://docs.sleeper.com/
- MySportsFeeds personal access: https://www.mysportsfeeds.com/register
- MySportsFeeds availability distinctions: https://www.mysportsfeeds.com/faq/
- SportsDataIO personal plans: https://sportsdata.io/developers

## Current environment

The workspace began empty with no .NET SDK. A project-local .NET 10 SDK is now
installed under .tools/dotnet, and the application and SQLite imports are built.
No external provider accounts or hosted deployment have been created.
