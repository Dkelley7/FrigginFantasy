# Verification — October 16, 2026

## Completed

- Launcher fix: reproduced the Windows .NET Runtime failure `address already in
  use` on port 5180 while the original app remained healthy. Verified repeated
  `.cmd` launch returns exit 0 and reuses the existing server, simultaneous cold
  launches start only one server, and an unrelated HTTP listener produces a clear
  exit-1 conflict while remaining untouched. Diagnostic logs capture launch output
  and failures; PowerShell syntax validation reports zero errors.

- Debug build: zero warnings, zero errors.
- Release publish to `artifacts/publish`.
- Published-output server smoke check: dashboard and fingerprinted Blazor script
  both return HTTP 200. The double-click launcher's PowerShell script restores
  packages, builds, and serves the main database successfully on localhost:5180.
- 33 domain / persistence / importer checks, including full 18-pick snake and
  salary competitions, simultaneous-pick serialization, stale revision rejection,
  mutual undo, budget reservation / rollback, kickoff locks after restart, scoring
  presets, K/DST scoring, and missing performance settlement.
- Real nflverse CSV fixtures: schedules, rosters, player/kicker and team statistics.
  Repeated imports preserve counts, corrections replace prior statistics, and
  projections neither consume current-week outcomes nor change after creation.
- Actual network synchronization of 2026 rosters and schedules plus 2025 and 2026
  player and team statistics completed successfully in the user's main database.
- 29 HTTP/database account checks cover in-app email registration, password
  validation, invitation restrictions, concurrent redemption, password hashing,
  roster ownership, preservation of existing competitions, sign-in, account
  lockout, password changes, logout, and account/session persistence after restart.
- Two additional browser account checks cover 390px registration layout and
  rejection of a rival-ready action after deliberately re-enabling the disabled
  button. Registration and account-settings screenshots were visually inspected.
- Chrome browser checks cover practice-room creation, readiness, picking, turn
  progression, mutual undo, a second connected client receiving updates, salary
  purchases, matchup/history screens, real NFL competition setup, and recent
  player statistics.
- Desktop and 390px-wide mobile screenshots were captured and inspected. The
  dashboard and draft room have no horizontal page overflow. No JavaScript
  exceptions were observed in the shared-screen browser test.

## Artifacts

Screenshots are intentionally local under `artifacts/`, excluded from Git:

- `desktop-dashboard.png`
- `desktop-draft.png` (fictional practice room)
- `desktop-real-draft.png` (imported NFL players and headshots)
- `desktop-salary.png`
- `desktop-setup.png`
- `desktop-settings.png`
- `player-stats.png`
- `mobile-dashboard.png`
- `mobile-draft.png`
- `account-register-desktop.png`
- `account-register-mobile.png`
- `account-settings.png`

## Repeat browser checks

The browser test uses `http://localhost:5180` and Chrome DevTools on port 9223.
For the shared-screen browser test only, set `Auth__Enabled=false`. Normal play
uses in-app accounts with authentication enabled. Run the test app with
`--DataDirectory <absolute-path-to-artifacts/browser-data>` to
keep all test competitions separate from the user's database. Populate this
directory with the app's `--sync-season 2026` command or `--import-dir` fixtures.
Then start the app on port 5180, launch Chrome headlessly with an isolated profile
under `.tools/browser`, and use:

```powershell
& ./.tools/dotnet/dotnet.exe tests/SundayRivalry.Checks/bin/Debug/net10.0/SundayRivalry.Checks.dll --browser
```

Use `--auth-browser` instead for account checks plus the browser authorization
check; it starts and stops its own isolated account-enabled server on port 5182.
Headless browser processes should be hidden and closed after checks.

## Limits of verification

The Dockerfile was prepared but Docker is not available for a container build in
this workspace. No hosting, remote HTTPS endpoint, external provider account, or
physical-phone network connection was provisioned. Mobile layout was tested
through Chrome device emulation. Scoring uses delayed published data and locally
generated pregame estimates; no real-time or professional-projection feed is
claimed. A live weekend has not elapsed while testing; lock and score transitions
were checked with controlled time and historical/imported statistics.
