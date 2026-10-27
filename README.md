# Pylon

**Changes, not polling.** Pylon watches external systems (GitHub first) and tells apps on your machine "this item may have changed", so each app stops running its own polling loop. It calls out through [Postern](../postern), so it never holds a secret, and it shows what it sees in a small floating stream you can open from the tray.

Status: walking skeleton. See [`docs/implementation-plan.md`](docs/implementation-plan.md) (status at the top) and [`docs/scope.md`](docs/scope.md).

## What's here

- **Monitors.** A monitor polls one source and turns what changed into events. The GitHub monitor watches a list of repositories through a Postern connection and reports, each switchable: PR opened, ready for review, review requested, reviewed, approved, checks (started, passing, failing), merged and closed. Bots and drafts can be kept quiet.
- **The stream widget.** Off by default; the tray icon toggles it. A topmost window that never steals focus, stays out of the taskbar and Alt+Tab, drags anywhere, resizes from any edge, snaps to screen edges, and switches between compact, expanded and full screen. Each record shows the monitor's icon and colour, a one-line summary and a status dot. Click one to see what Pylon observed: for a poll, the pull request before and after and the requests made; for a webhook, the body.
- **Subscribers** get hint events over `\\.\pipe\pylon` as server-sent events, with `since=` replay and `resync` when a gap can't be replayed. `Pylon.Client` handles the consumer contract for .NET apps.
- **The test bench** (`run bench`) is a fake GitHub you drive with buttons, plus a payload editor that sends webhooks or raw events straight into a dev Pylon.

## Development

Requirements: .NET 10 SDK, and a Postern checkout next to this one (`..\postern`; override with `-p:PosternRoot=...`).

```powershell
dotnet build pylon.slnx
dotnet test tests\Pylon.Tests\Pylon.Tests.csproj
.\run.cmd                                        # Pylon (Dev) in the tray, pipe pylon-dev, via Postern (Dev)
.\run.cmd bench                                  # the test bench
dotnet run --project tools\Pylon.Snapshots       # every window rendered offscreen into captures\
dotnet run --project tools\Pylon.E2E             # poll path end to end through a throwaway Postern (Dev), ~2 min
```

`pylon-cli` (in `src\Pylon.Cli\bin\...`):

```powershell
pylon-cli status
pylon-cli tail --topic "github/pr/example-org/*/*" --type "github.pr.*"
pylon-cli emit --file opened.json --github-event pull_request          # dev Pylon only
```

Debug builds run as **Pylon (Dev)**: pipe `pylon-dev`, data in `%LOCALAPPDATA%\Pylon.Dev`, Postern (Dev) on `postern-dev`, and dev ingest on. `Pylon.exe` options: `--headless`, `--pipe NAME`, `--data-dir DIR`, `--postern-pipe NAME`, `--dev-ingest`.

### Layout

| Path | What |
|---|---|
| `src/Pylon.Core` | Event contract, topic and type patterns, monitor model, stream filter. No Windows/UI deps |
| `src/Pylon.Sources.GitHub` | GitHub poll strategy, PR snapshots and the rules that turn two snapshots into events, webhook normaliser |
| `src/Pylon.Broker` | Named-pipe host, SSE, SQLite log and observation store, monitor scheduler, Postern gateway |
| `src/Pylon.App` | `Pylon.exe`: tray, stream widget, payload view, Monitors and General pages |
| `src/Pylon.Client` | What subscribing apps reference |
| `src/Pylon.Cli` | `pylon-cli`: status, tail, emit |
| `tools/Pylon.FakeGitHub` | Stateful fake of the GitHub REST slice Pylon uses, with ETags, 304s and webhook bodies |
| `tools/Pylon.TestBench` | The test bench app |
| `tools/Pylon.E2E` | The poll path with the real executables |
| `tools/Pylon.Snapshots` | Offscreen renders of every window |
| `tests/Pylon.Tests` | Unit tests plus integration tests through a real pipe |
