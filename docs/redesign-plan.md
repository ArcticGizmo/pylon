# Pylon redesign: implementation plan

A checkpoint plan for turning the user-journey mockups into the app. Hand this whole file to a new session; it is meant to be enough on its own.

## Start here (for the implementing session)

1. Read, in this order: this file, `README.md`, `docs/scope.md`, and the top of `docs/implementation-plan.md` (Status, decisions D1–D8).
2. The mockups are the visual and behavioural reference. They live as static HTML in `docs/design/pylon-journeys/project/*.dc.html` (one file per screen; `Main.dc.html` is the overview). The canvas is also at https://claude.ai/artifact/8NYiVq8yysrQpBuQA4uvBg. Read the screen's file before building it; match its layout, wording and states, not its exact pixels.
3. Work on a new branch from `overnight/skeleton`: `redesign/journeys`. Commit at the end of each checkpoint. Never push.
4. At the end of every checkpoint: run the tests, render the snapshots, commit, then **stop and report** (what changed, what was decided, snapshot paths, anything skipped). Wait for the user before starting the next checkpoint.

## Decisions already taken

- **Vocabulary.** Monitor → **Stream** (a configured thing to watch). Source = the kind (GitHub, Heartbeat, later Octopus and Graph). Subscriber → **Listener**. Test bench → **Simulator**. The user approved the mockups; confirm the internal rename (checkpoint 9) before doing it.
- **Navigation.** Main window tabs: **Activity** (home), **Streams**, **Listeners**, **Settings**, and in dev builds **Simulator**. Activity is the in-app message list; the floating widget becomes a mini Activity, not the only way to see events.
- **Plain words first, internals second.** Health, "why" and "how Pylon knew" are sentences. The existing poll journal views (Activity/Watching/Budget on a monitor today) move under **Diagnostics** on a stream.
- **"Why didn't I hear?" is answered per pull request** (a PR's story), with a one-click fix for each change kept quiet.
- Webhooks belong to streams, and the same change heard by polling and webhook is reported once (already built; see `MonitorHost.Receive` and `PublishPaired`).

## Still open (ask the user when the checkpoint reaches it)

- Should a read-only listener need approval at all? Default if unanswered: yes, the Postern pattern (ask once, remember, revocable).
- Renaming the consumer contract field `monitorId` to `streamId` (checkpoint 9). Pre-release, so allowed, but it changes `Pylon.Client`.
- Whether the separate `tools/Pylon.TestBench` app is deleted once the Simulator exists, or kept a while.
- The 7-day preview on the "what to hear about" step needs a look-back fetch. It is deferred unless the user asks for it.

## Ground rules for this codebase

- .NET 10, `TreatWarningsAsErrors` (xUnit analyzer rules included, for example `Assert.Single` over `Assert.Equal(1, x.Count)`).
- Views are code-only Avalonia (no XAML views). The palette and control factories live in `src/Pylon.App/Views/Ui.cs` (Emuwren's palette). Use `Ui.Selectable` for nav items and tabs, not a `Button`: Fluent repaints a button's background on hover, which fights the selected tint.
- Comments explain why, never what, and never what code used to do. Match the surrounding density and naming.
- Never query real GitHub (or any org repo) unless the user is present and asks. Use the fake GitHub (`tools/Pylon.FakeGitHub`), the `testbench` connection, or the Simulator.
- Event-from-a-delegate gotcha: `Polled?.Invoke(_journal.Add(poll))` skips the write when nothing is subscribed. Evaluate side effects first, then raise.
- Tests: `dotnet test tests\Pylon.Tests\Pylon.Tests.csproj` (100 passing at the start of this plan). Broker tests run the real pipe and the fake GitHub; follow `BrokerTests.Start(...)`.
- Snapshots: `dotnet run --project tools\Pylon.Snapshots` renders every window offscreen into `captures\`. If the user's Pylon is running it locks `src\Pylon.App\bin`; build elsewhere instead of stopping it: add `-p:BaseOutputPath=<a scratch folder>\` to the command. Add a snapshot for every new screen.
- When a build or command is blocked by endpoint security, say so and don't retry it reworded.

## Where things are now

| Area | Files |
|---|---|
| Event contract, stream (monitor) model, poll journal records | `src/Pylon.Core/Events/*`, `src/Pylon.Core/Monitors/MonitorDefinition.cs`, `Sources.cs`, `PollTrace.cs` |
| GitHub polling, change rules, webhook normaliser | `src/Pylon.Sources.GitHub/GitHubMonitor.cs`, `PullRequestDiff.cs`, `WebhookNormaliser.cs`, `GitHubSource.cs` |
| Scheduler, Postern gateway, pairing of poll and push, testbench connection | `src/Pylon.Broker/Monitors/MonitorHost.cs`, `PosternGateway.cs` |
| Storage: event log, payloads, fingerprints, poll journal | `src/Pylon.Broker/Storage/EventStore.cs`, `PollJournal.cs` |
| Pipe, SSE, dev ingest | `src/Pylon.Broker/Http/*` |
| App shell, tray, nav | `src/Pylon.App/App.axaml.cs`, `Views/MainWindow.cs` |
| Monitors page and per-monitor detail (becomes Streams + Diagnostics) | `Views/MonitorsPage.cs`, `Views/MonitorDetail.cs`, `Views/MonitorEditor.cs` |
| Floating widget and event detail window | `Views/StreamWindow.cs`, `Views/PayloadWindow.cs` |
| Test bench (becomes the Simulator) | `tools/Pylon.TestBench/*`, fake state in `tools/Pylon.FakeGitHub/*` |

## Checkpoints

Each one leaves the app working and shippable. Sizes are rough.

### 1. Words and navigation (small)

Mockups: any app screen's nav (`J2-Activity.dc.html`).

- Rename user-facing text only: Monitors → Streams, "Add GitHub pull requests" → "Add a stream", General → Settings. Code names, `monitors.json` and the contract stay as they are until checkpoint 9.
- Main window nav: Activity, Streams, Listeners, Settings, built with `Ui.Selectable`. Listeners shows an empty state ("No apps are listening yet") until checkpoint 8. Activity is a placeholder until checkpoint 2.
- Tray menu: "Open Pylon" (opens Activity), "Show widget", "Quit".
- Brand area as in the mockups: "Pylon", tagline "changes, not polling", "- DEV" in dev builds (already done).

Done when: nav matches the mockup, tests pass, snapshots for each tab.

### 2. Activity tab: the message list in the main window (medium)

Mockup: `J2-Activity.dc.html`. Empty state: `J1-Empty.dc.html`.

- Left pane: search box, filter chips (All, one per stream; "Needs me" arrives in checkpoint 6), events grouped by day, newest first, live as they arrive, paging older ones from the store. Reuse `StreamFilter` and the history paging in `StreamWindow`; pull shared list logic into one place rather than copying it.
- Right pane for the selected event: status, summary, stream, time, PR title; **Open on GitHub**; **This PR's story** (a stub until checkpoint 5); **How Pylon knew** as one sentence built from the journal entry that produced it (`PollJournal.PollFor(sequence)`, then its observation for that topic: "Pylon checked org/api at 10:42 and found a new review from sam that approves"); **What listening apps received** (the contract fields in mono); a link to the stream's diagnostics. **Who heard it** shows "You" only, until checkpoint 8.
- Empty state when there are no streams: the first-run screen, with "Add a stream" and (dev builds) "Try it with simulated GitHub".
- The widget gets an "Open Activity" action that opens the main window on this tab with the same event selected.

Done when: the user can see and inspect every event without the widget, tests pass, snapshots for list, detail and empty state.

### 3. Streams tab: health first, diagnostics second (medium)

Mockup: `J5-Streams.dc.html`.

- List of streams on the left (colour, name, today's count, a health word: Healthy, Waiting, Error, Paused). Detail on the right with **Overview** and **Diagnostics** tabs.
- Overview: one health sentence ("Healthy. Checked 20 seconds ago; next check in 40 seconds.", "Waiting for you in Postern: allow access to org2", with an action), request use per hour, today's told-you/kept-quiet counts (from the journal), heard-by, and a plain summary of what it tells you about. Actions: Check now, Pause/Resume, Edit.
- Diagnostics: the existing `MonitorDetail` Activity, Watching and Budget content, moved here unchanged.
- Kept-quiet counts need reasons grouped; that's cheap now (`Observation.Quiet`) and becomes exact once checkpoint 5 adds reason codes.

Done when: everything on today's Monitors page is reachable, the health sentence covers every `MonitorState`, tests pass, snapshots for healthy, waiting and error.

### 4. Add a stream: pick, don't type (medium–large)

Mockups: `J1-AddStream.dc.html`, `J1-WhatToHear.dc.html`.

- Two steps in the main window (not a dialog). Step 1: source cards (GitHub, Heartbeat), account, what to watch, repositories. Step 2: plain-language "tell me when" and "keep quiet about", name, colour.
- **Accounts:** read the Postern connections Pylon can use. Check `..\postern` for whether `Postern.Client` can list connections. If it can't, stop and ask the user whether to add that to Postern or to keep a typed connection name with a picker of names already used.
- **Repositories:** list them through Postern with `GET {connection}/user/repos?sort=pushed&per_page=100` (a new Postern scope; register it the same way `GitHubSource.ScopesFor` does). Show open PR count only if it's cheap; otherwise drop it. Searchable, multi-select. In dev builds the `testbench` connection lists the fake's repositories.
- **Watch modes:** build "pull requests in repositories I pick" now. "Waiting for my review" and "PRs I opened" need GitHub search (`/search/issues`) and a new strategy; list them as "coming soon" and leave them for a later checkpoint unless the user says otherwise.
- **New switches**, each a `MonitorDefinition.Options` entry with a rule in `PullRequestDiff` and `GitHubSource.Receive` and tests: "only when it's me" for review requests (needs the account's login: `GET {connection}/user`, cached per connection), and "only when they fail" for checks.
- Editing a stream reuses the same two steps. Retire `MonitorEditor`.
- The 7-day preview is out of scope (open question above). Keep its space for a "what this will tell you about" summary instead.

Done when: a stream can be created and edited end to end against the `testbench` connection without typing a repo name, tests cover the new switches, snapshots for both steps.

### 5. A pull request's story (medium)

Mockup: `J3-WhyNot.dc.html`.

- Opening a PR (from Activity search such as `org/web#88`, or "This PR's story") shows a timeline of every change Pylon saw on it, across all streams, newest last: what it told you (and which stream) and what it kept quiet, with the reason and a fix button.
- **Reason codes:** give `QuietChange` a machine-readable `Code` next to the human `Reason` (for example `switched-off`, `ignore-bots`, `drafts`, `checks-only-failing`, `reviews-only-me`, `already-reported`, `baseline`). Each code maps to a fix that edits that stream's definition ("Turn it on for Work PRs", "Stop ignoring bots", "Tell me when they pass too").
- **Lookup:** add a `poll_topics(poll_id, topic)` table to `PollJournal`, written in `Add`, so a PR's entries are found without scanning JSON. Reported events also come from the event log by topic.
- The journal keeps polls for 3 days. Say so on the story when it's older than that ("Pylon keeps this detail for 3 days").
- The summary banner: "Pylon saw N changes; told you about X; kept Y quiet".

Done when: for any PR in the fake, the story explains every reported and quiet change, each fix button works, tests cover the topic lookup and the reason codes, snapshot of a story with all three kinds.

### 6. The widget as a mini Activity, and "Needs me" (small–medium)

Mockup: `J2-Widget.dc.html`.

- The widget keeps its window behaviour (topmost, no focus steal, drag, resize, snap) and its list. It gets the stream colour bar per row, the All and Needs me chips, and Open Activity.
- **Needs me:** an event needs you when it's a review requested of your login, or a review, failing checks or merge on a PR you authored. Compute it when the event is published (the stream knows its account login from checkpoint 4) and store it with the event, not in the subscriber contract. Highlight those rows in both Activity and the widget.

Done when: Needs me is right for the fake's scenarios (tests), the widget matches the mockup, snapshot.

### 7. Simulator inside Pylon (medium)

Mockup: `J5-Simulator.dc.html`. Dev builds only.

- A Simulator tab hosting `FakeGitHubState` and `FakeGitHubApi` in-process on loopback (the library already exists; the separate bench app hosts it today). Creating the first simulated repo also creates a "Simulated GitHub" stream on the `testbench` connection, pointed at the in-process fake.
- Repos and PRs with action buttons, as the bench has today. "How changes reach Pylon": polling, webhooks (call `MonitorHost.Receive` directly), or both.
- **What just happened:** a numbered narrative of the last action: you did X; the stream checked N seconds later and found it (or the webhook arrived); it reported Y; who heard it. Build it from `MonitorHost.Polled` and `EventHub.Published`.
- The assumption quirks from the bench go under an "Advanced" expander.
- Ask the user whether to delete `tools/Pylon.TestBench` now. `tools/Pylon.E2E` and the tests keep using `Pylon.FakeGitHub` either way.

Done when: the whole poll and push path can be exercised from inside Pylon with no second app and no Postern, snapshot of the Simulator after an action.

### 8. Listeners (large)

Mockups: `J4-Ask.dc.html`, `J4-Listeners.dc.html`. This was Phase 5 of `docs/implementation-plan.md`.

- Ask the user the open question on approval first.
- **Identity:** the connecting process from the named pipe (client process id → executable path), as Postern does. Read `..\postern` for its approval flow, window and storage, and mirror them: a prompt window ("Perch wants to listen to Pylon", the topics it asks for, Allow/Don't allow, remember), an approvals file in the data directory, and refusal with a clear error over the pipe.
- `pylon-cli tail` is allowed for its session only, without being remembered.
- **Delivery tracking** for "Who heard it": per listener, the last sequence delivered and a count per day. An event was heard by a listener whose filter matched and whose delivered sequence passed it. A disconnected listener shows "waiting" with how many events are waiting for it (replay with `since=` already exists).
- Listeners tab: pending requests at the top, allowed apps with live status, what they hear, events today, last delivery, and Revoke (or Disconnect for a session-only listener).
- Activity's "Who heard it" then lists real listeners.

Done when: an unknown process is asked about, an allowed one is remembered, a revoked one is refused, tests cover each through a real pipe, snapshots of the prompt and the tab.

### 9. Internal rename and docs (medium, mechanical)

- Confirm with the user first, including the `monitorId` → `streamId` contract change.
- Rename types, files and members to match the UI: `MonitorDefinition` → `StreamDefinition`, `MonitorHost` → `StreamHost`, `MonitorDetail` → `StreamDiagnostics`, and so on. `monitors.json` → `streams.json`, reading the old name once and writing the new one. The journal's `monitor_id` column can keep its name, with a comment.
- Update `docs/scope.md` (vocabulary, Activity, listeners), `docs/implementation-plan.md` (status table, mark Phase 5 and the old Phase 2/4 items done or replaced), and `README.md`.

Done when: no user-facing or code name says "monitor" or "subscriber" except in migration code, tests pass, docs match the app.

### 10. Settings (small)

- Settings tab: Postern status and the scopes Pylon holds (moved off the Streams page), what Pylon keeps and for how long, widget options (show on start), the pipe name, the data folder, and in dev builds dev ingest and the Simulator's port.

Done when: nothing configurable is left on a page where it doesn't belong, snapshot.

## Not in this plan

- S1 against real GitHub (needs the user present; see "Measuring S1 in the app" in `docs/implementation-plan.md`). The Diagnostics views from checkpoint 3 are what it uses.
- Push mode through a relay, other sources (Octopus, Graph), desktop toasts, installer and release (Phase 6).
