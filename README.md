# FrigginFantasy

The source repository for **Sunday Rivalry**, a private NFL fantasy app.

A private NFL fantasy app for two people. Fresh teams every week, snake or salary
cap, and all the bragging rights. Built with .NET 10, Blazor Server, EF Core, and
SQLite. No paid API, separate database server, or JavaScript build step is required.

## Start on this desktop

Double-click **Start-SundayRivalry.cmd**, keep its terminal open, and visit
**http://localhost:5180**. Press Ctrl+C in the terminal to stop.

Opening the launcher again reuses the existing app and opens it in your browser.
Two simultaneous launches are serialized, so they cannot start duplicate servers.
If another program occupies the selected port, the launcher reports the conflict
without stopping that program. Startup/runtime output is saved under
`src/SundayRivalry/App_Data/logs/launcher-*.log`; failed launches keep the command
window open and show the log path. Pass `-NoBrowser` to suppress browser opening
or `-Port 5181` to choose a different port. Tailscale Serve must target whichever
port you choose.

The launcher prefers a system-installed .NET 10 SDK. If none is available, it uses
the SDK in `.tools/dotnet`, downloading it from Microsoft if necessary, and restores
NuGet packages. Installation and first data sync require internet access; saved drafts
and imported data remain usable offline. Google Fonts and player headshots have
system-font / initials fallbacks.

1. Choose **Create your account** and register with your Gmail or another email
   address, a display name, and a password just for Sunday Rivalry.
   From **My account**, create an invitation for your partner's email address.
   They open the link and create their own account. Both sign in separately.
2. Use **New competition** to choose the season, NFL week, included game days,
   snake or salary cap, and scoring preset. Individual games can also be selected.
3. Open the room and mark both participants ready.
4. In snake mode, take turns selecting players from your respective signed-in
   browsers. Salary mode builds the signed-in player's roster. Both teams may
   select the same player in salary mode. On a shared desktop, use separate
   browser profiles or a private window for the second account.
5. Use **Matchup & points** for imported scores and expandable scoring breakdowns.
   Find past competitions in **The record book**.

The two practice buttons create separate, clearly marked competitions with
fictional athletes. They do not affect your head-to-head record or first-pick rotation.

## Rules

- Nine starters: QB, RB, RB, WR, WR, TE, FLEX (RB/WR/TE), K, team D/ST. No bench.
- Full PPR default; half PPR and standard presets available per competition.
- Snake order: A, B, B, A, A, B… over nine rounds. Real snake competitions
  alternate their first participant. Picks are exclusive, and undo needs both
  participants. New picks cancel pending undo requests.
- Salary cap: 200 fantasy credits by default; adjustable from 50 to 1,000.
  Prices scale with the cap and estimated performance, and are frozen at creation.
  Every purchase reserves enough budget for the cheapest legal completion.
- All roster changes and undo stop at the **scheduled kickoff of the first
  included game**, enforced by the server even after restart. Empty slots score
  zero; no automated drafting or purchases occur.
- Offense: passing 0.04/yard, 4/TD, −2/interception; rushing/receiving 0.1/yard,
  6/TD; reception 1 / 0.5 / 0; lost fumble −2; two-point conversion 2; return TD 6.
- K: XP 1; FG under 40 yards 3, 40–49 yards 4, 50+ yards 5; no miss penalties.
- D/ST: sack 1; takeaway, safety, blocked kick 2; defensive/special-teams TD 6.
  Points allowed: 0 = 10; 1–6 = 7; 7–13 = 4; 14–20 = 1; 21–27 = 0;
  28–34 = −1; 35+ = −4. This app uses the **full opponent scoreboard total**,
  including scores allowed by the offense; it may differ from other platforms.
- Scores settle after the included games finish, feed coverage is available,
  and two days have elapsed since the last kickoff. An absent player row remains
  pending until completed player and D/ST feeds cover both teams, then settles to
  zero. Upstream corrections can update historical results. Exact ties remain ties.

## Data

FanDuel Research pregame CSV projections can be imported from **Data & settings →
Pregame projections**. Select the season, week, export scoring, and actual CSV
columns, then preview player matches before saving. Import before creating a
competition: existing competitions retain their original estimates and salaries.
Imports use the selected scoring preset only; unmatched players retain local
estimates. Verify the export's other scoring rules as well. The app never connects
to your FanDuel account. A real export is needed to verify source column/scoring
compatibility; importer checks use clearly synthetic fixtures.

Live-stat provider research, current free/paid options, request-budget examples,
and coverage limitations are in [live-data-options.md](docs/live-data-options.md).
Tank01 live box scores are supported. Run **Configure-Tank01.cmd** to store a
RapidAPI key with Windows user encryption, then restart the app. The key stays
under the ignored `.tools` folder and is loaded only into the server process;
it is never sent to phones. Environment variable `Tank01__ApiKey` is an alternative
for hosting elsewhere. The Windows-encrypted file works only for the Windows
account that created it.

Updates run at most every 30 minutes per relevant rostered game, between scheduled
kickoff and six hours later, stopping when Tank01 reports completion. Practice
rooms and page refreshes never trigger provider calls. Duplicate games across
competitions share a fetch. There are hard app-side limits of 900 attempts in any
rolling 31 days and 100 per UTC day. Every attempt is reserved in
`App_Data/tank01-usage.db` before sending, including failed requests; preserve this
file when moving or backing up the app. Changing the key or restarting never
resets the counter. Usage elsewhere on the same RapidAPI subscription is outside
this app's counter. A provider quota rejection pauses calls for 31 days.

**Data & settings → Game-day updates** shows usage, status and a pause/resume
control. Live stats stay separate from delayed historical imports. Partial scores
are explicitly marked, with missing categories shown under scoring breakdowns;
final results require historical reconciliation. Kicker distances use scoring
plays only when they reconcile with the explicit field-goal count. Some uncommon
categories are not reported by every box score and remain provisional.

The optional CLI `--validate-tank01` makes at most two budget-counted calls to
check a completed game; it saves response samples locally without changing league
scores. This is an explicit setup diagnostic, separate from scheduled polling.

[nflverse](https://github.com/nflverse/nflverse-data) provides public bulk CSVs:
rosters, schedules, player statistics (including kicking), and team statistics.
The app consumes these files directly in C#; no R or Python runtime is needed.

- Initial sync imports the requested season and prior-season statistics.
- Metadata refreshes approximately every 12 hours while running. Active, unsettled
  competitions trigger 15-minute import checks. **These are delayed statistics,
  not a live feed**; frequent polling cannot make the upstream data live.
- The **Sync NFL data** button runs an immediate import. Failed downloads keep
  cached data; partial imports report missing statistics. Imports upsert records
  so corrections replace prior values without duplicating them.
- Pregame estimates are the mean of up to five prior appearances under the chosen
  scoring preset, with a position baseline when history is absent. They are
  **locally generated estimates, not professional projections**. No opponent,
  depth-chart, or injury adjustment is applied.
- Schedule, roster, and estimate snapshots are stored per competition. Imports
  never change already drafted player identities, selected games, or prices.
- Names and metadata are local. Headshot references come from nflverse and load
  directly from their original CDN with browser caching; images are not downloaded
  into the database. Initials appear on failure. nflverse data and image rights
  remain subject to their respective owners' terms.
- Time displays use **America/New_York**; timestamps are stored in UTC.
- Closing the server stops imports. It catches up on subsequent starts.

You can inspect [nflverse's update schedule](https://nflreadr.nflverse.com/articles/nflverse_data_schedule.html).
No unsupported Sleeper/ESPN endpoints or paid providers are used.

## Other devices at home

Run this from the project directory:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/start.ps1 -Lan
```

Open `http://YOUR-DESKTOP-LAN-IP:5180` on a phone or another computer on the same
network. Windows may request a firewall rule. Each browser signs in to its own
player account. Draft changes are pushed to connected browsers. Keep the desktop
awake and the server running. For remote access, prefer Tailscale Serve forwarding
to localhost:5180 over HTTPS; the `-Lan` flag is unnecessary with Serve.

## In-app accounts

Sign-in is enabled by default, including localhost. Register the first account
at `/register` before sharing access. Gmail and other email addresses work as
usernames; this is email/password registration, not Google OAuth. Email ownership
is not verified and no email is sent. Use an app-specific password, not your Gmail
password. Passwords must be 12–128 characters and are hashed by ASP.NET Core Identity.

The first account owns roster slot 0; the invited account owns slot 1. The first
player can create an invitation on `/account`, specifying the partner's email.
Links expire in seven days, can be redeemed once, and are bound to that address.
New invitations revoke earlier unused links. Registration closes at two players.

For phone invitations, open `/account` using your Tailscale HTTPS address before
creating/copying the link. An invitation copied from localhost points to localhost
and won't reach the desktop from another device. Invitation links are shared
manually; the app does not send invitation emails.

Use **My account** to change your password or sign out. Email verification,
Google OAuth, and emailed password reset are not configured. Accounts, password
changes, and sessions persist after server restarts. Old environment-variable
password hashes and the terminal `--hash-password` setup are no longer used.

Cookies are HTTP-only and SameSite Strict. Forms require antiforgery tokens,
login is rate limited, and five failed attempts lock an account for 15 minutes.
Password changes invalidate other sessions when their security stamps are next
checked, approximately every minute, including connected Blazor circuits.
Loopback HTTPS-proxy headers are supported for Tailscale Serve. Keep the app
behind Tailscale or another HTTPS endpoint for remote access.

For isolated shared-screen development/browser tests only, `Auth__Enabled=false`
explicitly enables the former shared-desktop behavior. Keep authentication enabled
for normal play and remote access.

## Storage and hosting

Persistent files are in `src/SundayRivalry/App_Data/`: SQLite `rivalry.db` for games,
`accounts.db` for Identity accounts/invitations, WAL/SHM files when active, and
cookie-protection keys in `keys/`. Stop the app and
copy this entire directory for a consistent backup. This directory and `.tools/`
are excluded from source control. `DataDirectory` overrides the storage path.

One process and one persistent disk are sufficient. The database stores a
versioned, serialized game state in a SQLite row; writes are serialized and
published to clients only after saving. This intentionally suits a two-player
application. Multiple app replicas sharing a database are not supported.

An included Dockerfile prepares the app for a small Linux host:

```text
docker build -t sunday-rivalry .
docker run --rm -p 5180:8080 -v sunday-rivalry:/data -e AllowLan=true sunday-rivalry
```

For internet hosting, create the first account privately before sharing access,
place the app behind HTTPS, preserve `/data`, and use a single replica with WebSocket
support. Hosting has not been provisioned and the Docker build has not been run
in this Windows workspace. The .NET Release publish is verified separately.

## Development and verification

Open the workspace in VS Code, let C# Dev Kit load `SundayRivalry.slnx`, and press
F5 with the **Sunday Rivalry** launch configuration to build, run, and open the app.
If the SDK was installed while VS Code was open, use **Developer: Reload Window**
from the command palette. Fully exit and reopen VS Code if SDK detection still
reports the pre-installation state. All launch methods use localhost port 5180;
stop an existing app instance before starting another.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check.ps1
```

Double-click **Test-SundayRivalry.cmd** to run the complete isolated suite, including
simulated games, full snake and salary drafts, concurrency and database failures,
HTTP account checks, and two headless Chrome clients. Reports and replay seeds are
saved under `artifacts/verification`. The running app and your leagues are untouched;
no live API requests are made. See [testing instructions](docs/testing.md) for
extended runs, browser requirements, failure artifacts and continuous verification.

## Project layout

- `src/SundayRivalry/Domain`: competition models and scoring.
- `src/SundayRivalry/Services`: durable game mutations and bulk data imports.
- `src/SundayRivalry/Components`: dashboard, setup, draft, rosters, results.
- `tests/SundayRivalry.Checks`: domain, persistence, account, browser checks.
- `scripts`: local launch and verification.

See [the agreed plan](docs/implementation-plan.md) for the original requirements.

