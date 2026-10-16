# Repeatable verification

Double-click **Test-SundayRivalry.cmd**, or run from the project directory:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check.ps1
```

The command builds into `artifacts/check-build`, leaving the running app's binaries alone. It restores the normal project dependencies; there are no additional test packages. .NET 10 and Chrome are needed for the full suite. Set `SUNDAY_RIVALRY_CHROME` to an alternate Chrome executable if necessary. Chrome runs headlessly in a fresh profile, with two independent browser sessions. Your normal Chrome windows are untouched.

For an extended run with reproducible randomized drafts:

```powershell
./scripts/check.ps1 -Seed 42 -Iterations 50 -Repeat 3
```

Every run includes the 18 combinations of two draft formats, three scoring presets, and three slates (Thursday, Sunday, and Thursday/Sunday/Monday). `Iterations` adds randomized competitions. `Repeat` runs additional seeds, incrementing the supplied seed each time. The seed in a failed report can be passed back to replay the draft sequence.

For domain, rendering, feed, concurrency and HTTP account checks without Chrome:

```powershell
./scripts/check.ps1 -SkipBrowser
```

The script exits unsuccessfully if any group fails. A Windows GitHub Actions workflow runs the full suite on pushes and pull requests once the project is stored on GitHub. It can also be started manually there.

## Coverage

- Real `GameService` and SQLite: complete nine-player rosters for both participants, snake turn order and exclusivity, shared salary players, independent selections, budget reservations and averages, edits, stale revisions, mutual undo, and exact kickoff locks.
- Seeded draft simulations: different legal positional choices, FLEX feasibility, all scoring presets, selected-game eligibility, and privacy before kickoff versus roster reveal at kickoff.
- Simulated games: known offense, kicker and defense box scores checked against independent expected point totals, live corrections, provisional status, historical reconciliation after the publication buffer, DNP settlement, and restart persistence.
- Failure handling: simultaneous purchases cannot overspend; simultaneous rivals can select the same salary player; failed persistence cannot publish half a draft; a subsequent valid action still works; snapshots cannot modify server state.
- Rendered UI: each salary viewer's roster, budget, projections, pick history and matchup privacy; unaffordable players remain visible and disabled; snake picks stay visible.
- Accounts over real loopback HTTP: registration, invitations and redemption races, password hashing and changes, antiforgery recovery, lockout, logout, persistent sessions, and HTTPS forwarding used by Tailscale.
- Two real browsers: actual login forms, complete snake and salary drafts, live cross-client updates, rival-action tampering, salary privacy, mobile width, editing, kickoff reveal, simulated score updates, reload persistence, and JavaScript errors.
- Tank01 with mocked HTTP: quota reservation and persistence, concurrent monthly limits, daily caps, cooldowns, pause, active game windows, deduplicated competitions, incomplete payloads, kicker distances, last-good score retention, final-game polling stop, and provider 429 handling.

## Isolation and reports

Each HTTP/browser test uses a unique folder beneath `artifacts/test-runs` and an automatically assigned loopback port. Domain simulations create separate SQLite files under `artifacts/simulations` or the system temporary folder. Accounts and protection keys belong to those disposable test instances.

The test host disables synchronization workers, clears the configured Tank01 key, and replaces outbound HTTP with a handler that fails the test if called. Tank01 tests use a fake message handler. **No live API calls or monthly allowance are used.** There are no production test endpoints or simulation switches. The injectable application factory and clock are used by the test executable.

Group logs and machine-readable `results.json` are written to `artifacts/verification/<run>/`. Browser screenshots and failure page text live in that browser run's `artifacts/test-runs/<id>/` folder. These files are ignored by Git. Keep a failed run until it is diagnosed; older test artifacts can be deleted when no verification process is running.

These tests exercise the app on this machine. Before leaving for dinner, open your Tailscale URL on both phones over cellular, sign in, and confirm you can both enter the same competition. Leave the desktop awake, connected to the internet, and running the app. Actual provider availability, phone VPN connectivity and desktop sleep are outside the simulated suite.
