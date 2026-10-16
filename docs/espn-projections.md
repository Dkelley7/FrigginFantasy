# ESPN weekly projections

Sunday Rivalry can read player projections from a publicly viewable ESPN NFL fantasy league. It uses the public league request pattern implemented by the open-source [espn-api library](https://github.com/cwendt94/espn-api/blob/master/espn_api/football/league.py), directly from .NET. Python, ESPN cookies and a premium sports subscription are not required. This is an unofficial ESPN interface, so availability and schema stability are not guaranteed.

## Connect

1. In ESPN, create or use a League Manager football league for the current season. Under its Basic Settings, set **Make League Viewable to Public** to **Yes**. Verify that league pages open in a signed-out browser. ESPN documents league setup through its [support center](https://support.espn.com/hc/en-us/articles/360000087731-Create-a-League).
2. Copy the `leagueId` number from its URL. No personal league ID is included in the repository; enter your own in the app.
3. In Sunday Rivalry, open **Data & settings ? ESPN weekly projections**. Enter the league ID, season and NFL week, then select **Connect / refresh ESPN**.
4. Check the saved status and coverage notes. New competitions use matching ESPN estimates. Every competition keeps the estimates and prices captured when it was created.

The optional initial league ID comes from `Espn:LeagueId` in configuration (zero by default). On first startup with a synced upcoming schedule it seeds the connection with the next upcoming game's season and week. Afterwards the saved connection in SQLite takes precedence. Select the next NFL week in the panel when preparing the next week's competition.

## What is fetched and calculated

The importer requests up to 2,000 players, including rostered players rather than just 50 free agents. It does not need an undrafted league. The response must match the requested season and contain projection rows for the exact NFL week (`statSourceId=1`, `statSplitTypeId=1`). Actual scores and season totals cannot substitute for missing weekly projections.

Raw projected stat categories are passed through our own scoring code for standard, half PPR and full PPR. ESPN's league-specific `appliedTotal` is ignored. Each scoring format is saved as a separate projection set, including source and fetch time. Name, team and position must match uniquely against the cached NFL roster; unmatched and ambiguous entries cannot silently attach to another player.

- Offense uses projected passing, rushing, receiving, interceptions, lost fumbles, two-point conversions, and return touchdowns where supplied. Missing optional categories contribute zero to the estimate. Missing core categories or impossible negative projected event counts leave that player on the local fallback.
- Kickers use projected extra points and made field goals by distance. Distance totals must reconcile before a projection is accepted.
- D/ST currently keeps local estimates. ESPN's points-allowed probability ranges straddle our 20/21-point boundary, so applying our points-allowed function to the mean would not produce an exact expected score. The panel calls out this limitation.

This improves source freshness, not a guarantee of forecast accuracy. A fetch timestamp tells you when this app retrieved the data; ESPN's underlying model publication time is not asserted.

## Requests and caching

One successful response populates all three scoring formats. Successful refreshes have a one-hour cooldown, persisted in SQLite. A worker checks every 15 minutes while the configured week has upcoming games. The refresh button shares the same cooldown. Failures have a five-minute minimum retry delay; the worker retries on its next check. There are no immediate HTTP retries or redirects and no sign-in cookies are sent.

The ESPN connection is independent of Tank01 and does not consume its quota. A failed, empty, truncated or incompatible response retains the last good projection cache and existing competitions. Errors and player matching notes appear in the panel. The automatic connection needs the app running; saved projections survive restarts.

## Verification

`Test-SundayRivalry.cmd` includes mocked ESPN tests for weekly/source filtering, our three scoring presets, kicker distance reconciliation, duplicate matches, season validation, shared requests, persisted cooldown, fixed competition estimates and salaries, authentication errors, schema failures and recovery. The test host removes the ESPN worker and blocks outbound HTTP. Normal verification does not call ESPN or Tank01.

Live validation of a publicly viewable league on October 16, 2026 returned 1,052 player entries for week 6. With the local NFL roster, 311 players matched usable projections in each scoring preset. The cached response is under ignored `artifacts/espn-research`; it is not a permanent test dependency.



## Injury designations

The same ESPN response supplies current injuryStatus and injured fields, independently of projected stat availability. Questionable, Doubtful, Out, IR, Day-to-day, PUP and generic Injured labels are shown beside player names in the pool, selection dialog and visible rosters. Healthy records clear earlier badges; missing or unrecognized records retain the last known information. Fetch timestamps are shown in the draft and badge tooltips. Injury metadata updates in existing drafts without changing saved estimates, prices or picks. Current badges on results screens describe the latest injury report, not historical game availability. Refreshes use ESPN only, hourly while the configured week has upcoming games; they are not a real-time inactive feed.


Players currently designated Out or IR show 0 estimated points in the pool, selection dialog, roster estimates and projected team totals. Sorting uses this adjusted estimate. The saved estimate and salary remain intact, so clearing the designation restores the original estimate. Questionable and Doubtful do not force zero. Actual earned fantasy scores are calculated from statistics regardless of injury designation.

