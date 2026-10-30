# Pylon implementation plan

How to take `scope.md` from paper to a tray app that turns GitHub polling into granular events, shows them in a stream widget, and can be driven by a test bench. Spikes come first, riskiest first. Spikes are throwaway code under `spikes/<name>/`, each with a README that records **what it proved**, as in Postern. Nothing in a spike goes into `src/` without being rewritten.

## Status

Updated after the unattended run of 2026-10-27/28 (branch `overnight/skeleton`, not pushed). Everything below was built and tested against fakes only; nothing called real GitHub.

| Step | State |
|---|---|
| D decisions | ✅ D1–D7 taken as recommended (D4: project reference through `PosternRoot`); D8 (REST through Postern, no `gh`) agreed 2026-10-30 |
| S1 GitHub change detection | ⏳ **Needs you.** No spike: it's measured in the app itself (see "Measuring S1 in the app" below). The starting hypothesis is built and swappable; the assumptions it rests on are listed under "S1 assumptions" below |
| Webhooks belong to monitors | ✅ Added 2026-10-30. A delivery goes to every enabled monitor watching its repository, with that monitor's switches, and shows in its Activity as "received". The same change arriving both ways within 10 minutes is reported once. Deliveries no monitor watches are listed on the Monitors page |
| `testbench` connection | ✅ Added 2026-10-30. Dev builds only: a monitor on it polls the test bench directly, with no Postern setup. The Postern route is unchanged and still proved by `tools/Pylon.E2E` |
| Monitor detail page | ✅ Added 2026-10-30. Activity (every poll: requests, ETags, rate cost, what changed per PR, what was reported or kept quiet and why), Watching (each monitor's stored fingerprints) and Budget (requests per hour, 304 share, A5 evidence). Backed by a poll journal in `pylon.db` |
| S2 Event delivery over the pipe | ✅ Folded into Phase 1 and proved by integration tests (live, replay, pruned gap, unknown sequence, slow subscriber, stop ends streams). Node subscriber not tried |
| S3 Floating stream widget | ✅ Folded into Phase 2. Proved offscreen (snapshots) and live (Pylon.exe stayed responsive through 3,000 events at ~1,000/s with the widget open). **Needs you** for feel: focus, drag, resize, snap, DPI across monitors |
| S4 Postern from Pylon | ✅ Scopes derived per owner, registration, re-registration only when not covered, waiting/denied/not-running states, "Ask Postern again". Proved end to end by `tools/Pylon.E2E` against Postern (Dev) |
| S5 Push paper check | ⏳ Not done. The webhook normaliser and its templates were written from the payload shapes GitHub documents, without the relay comparison |
| Phase 1 walking skeleton | ✅ Core, Sources.GitHub, Broker, Client, Cli; 81 tests |
| Phase 2 stream widget + test bench (sender) | ✅ |
| Phase 3 GitHub monitors | ✅ First pass, on the S1 hypothesis |
| Phase 4 test bench (fake GitHub) | ✅ Including the "done when", automated: `tools/Pylon.E2E` |
| Phase 5 consumers | ⏳ `Pylon.Client` (`RunAsync` follows the consumer contract) only. No subscriber approval yet, so any process running as you can subscribe. Perch untouched |
| Phase 6 release | Not started |

## Overnight run: what to look at first

1. **Run it.** `.\run.cmd` starts Pylon (Dev); `.\run.cmd bench` starts the test bench. Add a heartbeat monitor to see the stream move, or follow the bench's "Postern setup for polling…" to poll the fake through your Postern (Dev).
2. **Renders** of every window are in `captures/` (offscreen, made-up data; regenerate with `dotnet run --project tools\Pylon.Snapshots`).
3. **The end to end:** `dotnet run --project tools\Pylon.E2E` (about two minutes). It runs Postern (Dev) headless on its own pipe and data folder, auto-approves only `pylon`, stores one throwaway credential for the fake and deletes it again.

### Decisions made without you (easy to change)

- Monitors call their list `targets`, not `repos`, so other sources can reuse the shape. GitHub reads them as `owner/repo`.
- A resync Pylon raises itself (a pruned replay gap, a slow subscriber) has source `pylon` and goes to every subscriber.
- Without `since`, a subscription is live only. The contract already says to refresh fully on connect, and `PylonClient.RunAsync` does that first.
- `ignoreBots` (on by default) silences PRs opened by bots and reviews by bots. `includeDrafts` (off by default) keeps drafts quiet until they're marked ready.
- A PR first seen open after the baseline is `opened` only if it was created after the baseline; an old PR that drifts into the 50-PR window isn't news.
- Checks report `pending` only for a new head commit. On the same commit only a passing/failing result that differs from the last one reported counts, so re-runs that end the same way are quiet.
- Payloads are cut at 512 KB each and kept 7 days or 200 MB, whichever comes first; events 30 days or 100,000.
- The poll journal keeps polls for 3 days. Response bodies (most of its size) are kept for 1 day or 100 MB, newest first, so on a busy setup only the most recent bodies are there to open.
- Dev ingest (`/_pylon/dev/*`) is always on in Debug builds and off in Release unless `--dev-ingest` is passed.
- A monitor is "what to watch"; polling and webhooks are two ways of hearing about it (agreed 2026-10-30). A webhook for a repository no enabled monitor watches reports nothing. A raw event (`/_pylon/dev/emit`) still goes straight in without a monitor.
- An event from one path is paired with the same change (topic, type and hint) arriving the other way within 10 minutes, and the second is kept quiet with the first's sequence as the reason. A pairing is used once, so checks failing then passing again still report.

### S1 assumptions (confirm with real repos, with you there)

| # | Assumption | What breaks if it's wrong |
|---|---|---|
| A1 | Submitting a review bumps the PR's `updated_at` | Reviews and approvals are only refetched when `updated_at` moves, so they'd be missed until something else touched the PR |
| A2 | A check run finishing does *not* bump `updated_at` | Harmless if wrong (checks are polled every time anyway), but then the checks calls could be gated too, which saves requests |
| A3 | Requesting a review bumps `updated_at` and shows in the list's `requested_reviewers`/`requested_teams` | Review requests would be missed |
| A4 | The pulls list's ETag changes whenever any PR in the top 50 changes | Changes would hide behind 304s |
| A5 | 304s through Postern don't count against the rate limit | Polling cost is higher than planned |
| A6 | Fine-grained PAT needs only Pull requests, Checks, Commit statuses and Metadata (all read) | 403s |
| A7 | Polling every 60s with one list + (open PRs × 2) checks calls per repo stays well inside the budget for ~20 repos | Secondary rate limits; the scheduler would need a global concurrency cap (it has jitter and backoff, not a cap) |

### Measuring S1 in the app

Every poll is journalled, so S1 needs no separate poller. On a GitHub monitor, **Check assumptions** (in its editor) makes it do the work the hypothesis skips, and flag wherever the hypothesis would have missed something:

| Assumption | How the app checks it | Where it shows |
|---|---|---|
| A1 | Fetches reviews for every open PR on every poll (304s when unchanged), and flags a new review whose PR's `updated_at` didn't move | Activity: an `A1` warning on the PR, and an "assumption broken" chip on the poll |
| A2 | Notes when checks changed on the same commit, with or without `updated_at` moving | Activity: `A2` notes |
| A3 | Flags requested reviewers changing without `updated_at` moving | Activity: `A3` warning |
| A4 | Whenever the pulls list answers 304, fetches it again unconditionally and compares | Activity: an `A4` warning on the poll, and the extra request marked with its purpose |
| A5 | Each call's cost is the drop in the remaining limit since the call before it in the same poll | Budget: "Are 304s free?" |
| A6 | Any 403 shows with Postern's error code on the request | Activity: the request row |
| A7 | Requests per hour that count against the limit, and the lowest remaining | Budget |

The test bench has a switch for each of A1, A2 and A4 that makes the fake GitHub break it, so the checks can be seen working before any real repository is involved.

The session: a sandbox repo with an Actions workflow, a fine-grained read-only PAT in a Postern (Dev) connection, a monitor on it with **Check assumptions** on and a 10s interval, and a second account to request reviews from and approve. Then a working day on the real repos (with you there) for A5 and A7, with the check off to see the true cost.

### Known gaps

- No subscriber approval or caller identity yet (Phase 5). The pipe is limited to your account, as Postern's is.
- No relay or push mode; webhooks only arrive through dev ingest.
- The widget's "unread" state is only the tray badge while the widget is closed; records inside the widget aren't marked unread.
- No global concurrency cap across monitors (A7).
- Icons are drawn from path data at runtime; there's no `.ico` pipeline like Postern's yet (Phase 6).
- Headless rendering found a real crash: Avalonia builds a row template with null content while it recycles containers. Fixed, and crashes now go to Pylon's own log.

## What this adds to the scope

The scope describes Pylon as invisible plumbing. Three additions make its value visible and testable:

1. **Monitors**: a configured instance of a source adapter. "Watch `org/perch` and `org/postern` through the `github-work` connection, every 60 seconds, for PR opened, review requested, approved and merged."
2. **The stream widget**: a floating, resizable window (off by default) that shows events as they arrive, can be filtered, and opens the payload behind any event.
3. **The test bench**: a separate Avalonia app with its own HTTP server that sends chosen payloads to Pylon on demand, and later plays a stateful fake GitHub for the poll path.

Two tensions with the scope, and how this plan resolves them:

- **"Hints, not data" vs "see the payload".** Pylon keeps what it observed (the raw response slice, the before/after diff, or the webhook body) in its own **observation store**, with short retention. Only the stream widget reads it. Subscribers still get hint events and nothing more. The consumer contract doesn't change.
- **Granular GitHub events vs cheap polling.** "Approved" and "checks failing" aren't visible from a single conditional request. The scope's ETag-only model may not be enough, so S1 measures it before anything is built.

## D. Decisions to agree before building

- [x] **D1. Event `type` field.** Add a dotted type to the contract (`github.pr.approved`) next to `kind`, so monitors and subscribers can filter granularly. *Recommendation: yes. Update `scope.md`.*
- [x] **D2. Observation store.** Payloads live in a separate SQLite table, linked by `sequence`, pruned by age and size (for example 7 days or 200 MB), never sent over the pipe to subscribers. *Recommendation: yes.*
- [x] **D3. Subscriber transport.** Server-sent events over HTTP over `\\.\pipe\pylon`, with `Last-Event-ID` as the `since=` sequence. Works for .NET and for Node (`socketPath`). *Recommendation: SSE, confirmed by S2.*
- [x] **D4. How Pylon references `Postern.Client`.** Project reference through a `PosternRoot` MSBuild property defaulting to `..\postern`, and CI checks out both repos. Move to a package if a third consumer appears. *Recommendation: project reference.*
- [x] **D5. Postern scope granularity.** Per owner (`GET github/repos/org/*/pulls/**`) so adding a repo under a known owner doesn't trigger a new Postern approval, while a new owner does. *Recommendation: per owner.*
- [x] **D6. Monitor storage.** `monitors.json` in the data directory, written by the UI and editable by hand, like Postern's `providers.json`. *Recommendation: yes.*
- [x] **D7. Same shape as Postern.** .NET 10, `TreatWarningsAsErrors`, one per-user Avalonia tray process hosting the broker, code-only views, Debug builds run as **Pylon (Dev)** on `pylon-dev` against `postern-dev`, with their own data. *Recommendation: yes.* Exception (2026-10-30): the palette and page furniture are Emuwren's (blue-tinted, lighter, 32px page margins, 12px panels), not Postern's.
- [x] **D8. GitHub access.** Pylon calls the REST endpoints directly through a Postern connection holding a fine-grained, read-only PAT. It never shells out to `gh`: that would bypass Postern's scopes and rate-limit view, use `gh`'s broad `repo` token, make ETag/304 polling awkward, and spawn a process per poll. Any user choice of credential (PAT, or borrowing the `gh` login) belongs in Postern's connection setup, not in Pylon. `gh webhook forward` stays a candidate for push (S5).

### Proposed event shapes

What a subscriber gets (the scope's contract plus `type` and `monitorId`):

```json
{
  "sequence": 18342,
  "source": "github",
  "monitorId": "gh-work-prs",
  "topic": "github/pr/org/perch/412",
  "type": "github.pr.approved",
  "kind": "changed",
  "resourceId": "https://github.com/org/perch/pull/412",
  "hint": { "reviewDecision": "approved" },
  "observedAtUtc": "2026-10-21T09:14:03Z"
}
```

What the stream widget gets on top, internal to Pylon: `summary` ("org/perch#412 approved"), `status` (see below), `via` (`poll`, `push` or `test`), and an `observationId` for the payload.

### Proposed GitHub event types

| Type | Kind | Status in widget | Hint |
|---|---|---|---|
| `github.pr.opened` | created | info | `{ "draft": false }` |
| `github.pr.ready_for_review` | changed | info | |
| `github.pr.review_requested` | changed | attention | `{ "reviewer": "<login or team>" }` |
| `github.pr.reviewed` | changed | info | `{ "state": "commented" \| "changes_requested" }` |
| `github.pr.approved` | changed | success | `{ "reviewDecision": "approved" }` |
| `github.pr.checks` | changed | pending / success / failure | `{ "checks": "pending" \| "passing" \| "failing" }` |
| `github.pr.merged` | changed | merged | |
| `github.pr.closed` | removed | neutral | |

Each one can be switched on or off per monitor, and each monitor is limited to a list of repos.

### Proposed monitor shape

```json
{
  "id": "gh-work-prs",
  "name": "Work PRs",
  "source": "github",
  "connection": "github-work",
  "icon": "pull-request",
  "accent": "#2f81f7",
  "repos": ["org/perch", "org/postern"],
  "events": ["opened", "review_requested", "reviewed", "approved", "checks", "merged"],
  "filters": { "includeDrafts": false, "ignoreBots": true },
  "pollSeconds": 60,
  "enabled": true
}
```

Repo wildcards (`org/*`) need an extra listing call and a refresh of the repo set, so they wait until after v1.

## Phase 0: spikes, riskiest first

### S1. GitHub change detection (load-bearing; do this first)

The question: what is the cheapest set of calls that detects every event type above, for 5 and for 20 repos, through Postern?

Candidate building blocks:

| Block | Detects | Cost |
|---|---|---|
| A. `GET /repos/{o}/{r}/pulls?state=all&sort=updated&direction=desc&per_page=50` with ETag | opened, closed, merged, and anything that bumps `updated_at` | One call per repo per tick. A 304 is free |
| B. `GET /pulls/{n}/reviews` and `/pulls/{n}/requested_reviewers` with ETag | reviews, approvals, review requests | Only for PRs that A says changed |
| C. One GraphQL query per repo: open PRs with `reviewDecision`, `reviewRequests`, `latestReviews`, `statusCheckRollup` | everything at once | GraphQL points, no 304 |
| D. `GET /commits/{sha}/check-runs` and `/commits/{sha}/status` per open PR head, with ETag | checks | One call per open PR per tick. A 304 is free |
| E. `GET /repos/{o}/{r}/events` with ETag and `X-Poll-Interval` | opened, closed, reviews in one feed | Cheap, but documented as not real-time |
| F. `GET /notifications` | review requests for the signed-in user | Cheap, but only about you |

Starting hypothesis: A as the gate, B (or C) for PRs that changed, D for checks on open PR heads.

- [ ] Find out which activities bump a PR's `updated_at` (review submitted, review requested, check run completed, push to head). This decides how much A can gate.
- [ ] Build each strategy as a small poller through Postern (extend the C3 poller in `..\postern\spikes\consumers\Poller`).
- [ ] Run against real repos for a working day. Record requests per hour, rate-limit units used (304s excluded), GraphQL points, and detection latency per event type.
- [ ] Define the **fingerprint** per event type: which fields count as a change, and which are noise (bot reviews, `mergeable` computed lazily, check re-runs with the same outcome).
- [ ] Check secondary rate limits when 20 repos poll at once. Decide on jitter and a concurrency cap.
- [ ] Record the fine-grained PAT permissions needed (expect Pull requests, Checks, Commit statuses and Metadata, all read).
- [ ] Record the Postern scopes actually used, for D5.

Done when: a written choice of strategy, a cost table, and fingerprint rules per event type.

### S2. Event delivery over the pipe

- [ ] Kestrel on a named pipe serving SSE (`text/event-stream`) with several concurrent subscribers, from .NET and from Node.
- [ ] Replay: reconnect with `Last-Event-ID` and get the gap from SQLite, or `resync` if the gap was pruned.
- [ ] Slow consumer: a subscriber that stops reading gets dropped once a bounded channel fills, and gets `resync` on reconnect. The other subscribers aren't held up.
- [ ] Topic wildcard matching (`github/pr/org/*/*`) plus type filters, with tests.
- [ ] SQLite write rate for the log with WAL, at a burst of 100 events per second.

Done when: a .NET and a Node subscriber survive a Pylon restart with correct replay, and a stalled subscriber is dropped without affecting others.

### S3. Floating stream widget (Avalonia)

- [ ] A borderless, topmost window that doesn't take focus when it appears (`ShowActivated = false`, plus `WS_EX_NOACTIVATE` and `WS_EX_TOOLWINDOW` so it stays out of the taskbar and Alt+Tab).
- [ ] Resize from edges and corners (borderless windows need custom grips), drag to move, snap to a screen corner.
- [ ] Three sizes: **compact** (the last few events), **expanded**, and **full screen** on the current monitor. Remember bounds per monitor and DPI.
- [ ] A virtualised list holding 10,000 records at a burst of 50 events per second without the UI stalling. Cap the in-memory list and page older records from SQLite.
- [ ] The record template: monitor icon with its accent colour, one line of summary, a status dot, and relative time. Unread records are marked.
- [ ] Payload view: pretty-printed JSON with folding and copy, plus a before/after diff for poll events. Check whether AvaloniaEdit is worth the dependency or whether a plain `SelectableTextBlock` is enough.
- [ ] Filter bar: monitor, repo, type, status, free text, plus pause.
- [ ] Look at Postern's `Views/Ui.cs` and reuse its look so the two apps match.

Done when: the widget runs fed by a timer that generates fake events, and passes the volume test.

### S4. Postern from Pylon (small; C3 already de-risked most of it)

- [ ] Reference `Postern.Client` the way D4 says, and build in CI.
- [ ] Derive scopes from the enabled monitors, register, and re-register when a monitor adds a new owner. Show "waiting for Postern approval" on the monitor while it's pending, and "denied" if it's refused.
- [ ] Postern not running, revoked, or rate-limited: the monitor backs off, shows why, and Pylon sends `resync` for that source when it recovers.

### S5. Push (paper only for now)

- [ ] Compare `gh webhook forward`, smee, Hookdeck CLI and Webhook Relay for getting GitHub webhooks to the desktop. Note what each needs (repo admin rights, an account, a public URL).
- [ ] Collect real webhook bodies for `pull_request`, `pull_request_review`, `check_suite`, `check_run` and `status`. These become the test bench's templates and the push normaliser's test fixtures.
- [ ] Note the open question on webhook signature checks (Postern's raw-secret tier, or a Postern verify endpoint).

## Phase 1: walking skeleton

```
src/Pylon.Core            event contract, topic and type matching, fingerprints, monitor model. No Windows or UI deps
src/Pylon.Sources.GitHub  GitHub poll adapter and (later) the webhook normaliser
src/Pylon.Broker          named-pipe host, SSE subscriptions, SQLite log and observation store, monitor scheduler
src/Pylon.App             Pylon.exe, the Avalonia tray app: Monitors, Apps, General pages and the stream widget
src/Pylon.Client          consumer library: subscribe, since=, resync handling, retry
src/Pylon.Cli             pylon-cli: status, tail, emit (Debug only)
tools/Pylon.TestBench     Avalonia app plus Kestrel: payload sender, then fake GitHub
tests/Pylon.Tests
spikes/
```

- [ ] Solution, `Directory.Build.props`, `global.json` and CI to match Postern.
- [ ] Event contract and topic/type matching in Core, with tests.
- [ ] SQLite event log and observation store with retention.
- [ ] Pipe host with SSE subscribe and replay, using what S2 proved.
- [ ] A built-in **heartbeat** source (one event a minute while enabled) so the whole path works with no external system.
- [ ] A dev ingest endpoint, `POST /_pylon/dev/ingest/{source}`, only in Debug builds or with `--dev-ingest`. It runs a payload through the same normaliser a webhook will use later, and tags the event `via: test`.
- [ ] `pylon-cli tail` prints events as they arrive.
- [ ] Tray app shell: tray icon, single instance, General page, `--headless`, Pylon (Dev) identity.
- [ ] Integration tests through a real pipe: subscribe, replay, resync on pruned gap, slow-consumer drop, dev ingest disabled in Release.

## Phase 2: stream widget and test bench sender

These come before the GitHub monitors because they make everything after them easier to see and debug.

**Stream widget**

- [ ] Build the widget for real, using what S3 proved. Off by default, toggled from the tray menu.
- [ ] Records from the live feed in-process (the widget doesn't go through the pipe), with history paged from SQLite.
- [ ] Payload detail view, with "payload pruned" when retention has removed it.
- [ ] Filters remembered between sessions.
- [ ] The tray icon shows a small badge for unread events while the widget is closed.
- [ ] Snapshot renders to `captures/`, like `Postern.Snapshots`.

**Test bench v1: payload sender**

- [ ] An Avalonia window with a template picker (the S5 webhook bodies, plus a raw Pylon event), a JSON editor, and **Send**.
- [ ] Fields to change the common parts quickly: repo, PR number, actor, review state, check conclusion.
- [ ] Sends to Pylon's dev ingest endpoint over `pylon-dev`, and shows the response and the resulting sequence.
- [ ] Saved sequences ("open, request review, approve, merge") that replay with a delay between steps, for demos.

Done when: you can click through a PR's life in the test bench and watch each step appear in the widget with the right icon, text and status, and open its payload.

## Phase 3: GitHub monitors

- [ ] The GitHub poll adapter, using the strategy and fingerprints from S1.
- [ ] Per-resource fingerprint state persisted, so a Pylon restart doesn't replay old changes as new events. The first poll of a new monitor sets a baseline and emits nothing.
- [ ] Each event type above, switchable per monitor, limited to the monitor's repos.
- [ ] Optional filters: drafts, bots, and "review requested of me".
- [ ] Scheduler: per-monitor interval with jitter, a global concurrency cap, backoff from `Postern-RateLimit-*` headers.
- [ ] `resync` when a monitor's source has been unreachable or rate-limited and comes back.
- [ ] **Monitors page**: add, edit, enable and disable monitors. Pick the Postern connection, repos, event types, interval, icon and accent. Show each monitor's last poll, last event, rate-limit use and Postern approval state.
- [ ] Scope derivation and re-registration from S4.
- [ ] Tests against recorded GitHub responses for every event type, including noise that must *not* produce an event.

## Phase 4: test bench v2, a stateful fake GitHub

- [ ] Kestrel inside the test bench serving the GitHub endpoints the S1 strategy uses, with real ETags and 304s and rate-limit headers (build on `..\postern\tools\Postern.FakeUpstream`).
- [ ] A scenario panel: repos and PRs whose state you change with buttons (open, request review, review, approve, push, checks pass or fail, merge, close).
- [ ] A request log showing what Pylon polled and whether it got 200 or 304.
- [ ] Setup steps: a `github-fake` connection in Postern (Dev) pointing at `http://127.0.0.1:<port>` (Postern allows plain http on loopback), and a monitor using it.

Done when: changing a PR in the test bench produces the matching event in the widget within one poll interval, through Postern, with no real GitHub involved.

## Phase 5: consumers

- [ ] Per-app approval for subscribers, the same pattern as Postern: requested topic patterns, an approval window, an app token, caller process checks.
- [ ] **Apps** page: approved subscribers, their topics, connected or not, and revoke.
- [ ] `Pylon.Client` for .NET: subscribe, reconnect with `since=`, `resync` callback, and a slow fallback refresh helper to make the consumer contract easy to follow.
- [ ] Perch as the first subscriber, moving its GitHub polling into Pylon (scope stage 1).
- [ ] A Node example over `socketPath`.

## Phase 6: release

- [ ] Installer, Run-key auto-start and `--supervise`, in-app updates, the same way Postern does them.
- [ ] Icons from one SVG, a Dev variant, and README.
- [ ] `scripts/smoke.ps1`: real exes end to end, including a Pylon crash and a Postern crash with replay and resync.

## Deliberately deferred

Push mode through a relay (after S5's paper check), repo wildcards, Actions runs, Octopus and Graph sources, desktop toasts, global hotkey for the widget, and out-of-process adapters. None of these change the contract, the store or the widget.

## Exit criteria

- [ ] Two GitHub monitors (personal and work connections) run for a week through Postern, within an agreed rate-limit budget.
- [ ] Every event type in the table is seen live at least once, and none of them fire on noise.
- [ ] The widget shows each event with the right monitor icon, summary and status, and opens its payload.
- [ ] The test bench drives every event type through both the dev ingest path and the fake GitHub poll path.
- [ ] A Pylon restart and a Postern restart each recover by themselves, with replay or `resync` as the scope says.
- [ ] Perch receives events as a subscriber and still keeps its slow fallback refresh.
- [ ] `scope.md` updated with the `type` field, the observation store, the stream widget and the scopes Pylon actually needed.

## Suggested order

```
D decisions ──► S1 ──────────────────────────► Phase 3 ──► Phase 4 ──► Phase 5 ──► Phase 6
           ├──► S2 ──► Phase 1 ──► Phase 2 ──┘
           ├──► S3 ─────────────────┘
           ├──► S4 ───────────────────────────┘
           └──► S5 (paper, any time before Phase 2's templates)
```

S1 runs longest (a working day of real polling), so start it first and build S2 and S3 while it runs.
