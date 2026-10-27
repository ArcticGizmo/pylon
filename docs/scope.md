# Pylon scope

A local event broker. Pylon watches external systems (GitHub, Octopus Deploy, Outlook and so on) and tells
interested apps on the machine **"this item may have changed"**. Apps then fetch whatever they need for
themselves.

Sibling product: **Postern** (`../postern`), the local credential broker. Pylon makes every external call through
Postern, using ordinary scopes like any other app. **Pylon never holds a secret.**

## The problem

Every app that wants to react to an outside change builds its own polling: its own schedule, its own
credentials, its own rate-limit handling, its own change detection. The cost grows as **apps × sources**. Several
apps end up polling the same API for the same things, and each one is another place to configure.

## What Pylon is

- **Source adapters**, one per integration. Each one turns a provider's state into change events.
- **Hint events, not data.** An event says what changed and where (`source`, `topic`, `resourceId`), plus at most
  a small summary. Pylon is never the source of truth.
- **Topic subscriptions** for local apps, limited by per-app approval.
- **A replay log** so a consumer that reconnects can catch up from where it stopped (`since=<sequence>`).
- **Change detection done once:** conditional requests (ETags), fingerprints, and diffing so only *real* changes
  produce an event.

## What Pylon is not

- **Not a data store or cache for consumers.** If an app needs the full PR, it fetches it through Postern.
- **Not a guaranteed-delivery queue.** Events are hints. Consumers must stay correct when they miss some (see
  consumer contract).
- **Not cross-machine.** One user, one machine.
- **Not a credential holder.** All auth goes through Postern.

## Event contract

```json
{
  "sequence": 18342,
  "source": "github",
  "topic": "github/pr/org/perch/412",
  "resourceId": "https://github.com/org/perch/pull/412",
  "kind": "changed",
  "hint": { "checks": "failing" },
  "observedAtUtc": "2026-10-21T09:14:03Z"
}
```

- `kind` is `changed`, `created`, `removed` or `resync`.
- `hint` is optional and small. Anything larger means the consumer should fetch.
- `resync` tells consumers to refresh fully. Pylon sends it when its sequence has a gap (after a restart, a
  pruned log, or a source outage).

Example topics:

| Source | Topic shape |
|---|---|
| GitHub PRs | `github/pr/{owner}/{repo}/{number}` |
| GitHub Actions | `github/run/{owner}/{repo}/{runId}` |
| Octopus | `octopus/deployment/{space}/{project}/{environment}` |
| Outlook mail | `graph/mail/{folder}` |
| Outlook calendar | `graph/calendar/{calendarId}` |

Consumers can subscribe with wildcards (`github/pr/org/*/*`), but only within the topics they're approved for.

## Source adapters

Each adapter declares a **mode**, so polling sources and push sources look the same to consumers:

| Mode | How | When |
|---|---|---|
| **Poll** | Scheduled requests through Postern, with ETag/`If-None-Match` and delta queries where the provider has them | v1. Every source works this way at first |
| **Push** | Webhooks arriving through an outbound relay connection, plus a slow reconcile poll | Later, per source. A desktop can't receive webhooks directly |

Change detection uses a fingerprint per resource, which Pylon compares on each poll. Perch's
`GitHubAlertsClassifier.Fingerprint` is the prior example: decide what "changed" means for that resource and
ignore noise (bot activity, lazily computed fields).

Tools to use where they fit:
- GitHub: conditional requests. A 304 doesn't count against the rate limit.
- Graph: delta queries.
- Octopus: the events API.

**Adapters don't hold secrets.** Each adapter's calls go through Postern under Pylon's scopes, so a buggy or
hostile adapter can only make requests those scopes allow. Third-party adapters might later run out of process.

## Subscription and access

- Local IPC over a named pipe (`\\.\pipe\pylon`) with an ACL limited to the current user's SID. No TCP listener.
- Per-app approval, as in Postern: the app asks for topic patterns, the user approves, and Pylon issues an app
  token. Pylon also checks the caller's process.
- An event's topic and `resourceId` can be sensitive (a mail folder or repo name), so subscriptions are limited
  even though the events carry no data.
- Malware running as the same user is out of scope, as in Postern.

## Consumer contract

Every consumer must:
1. **Refresh fully on connect and on every `resync`.**
2. **Treat an event as a reason to refetch**, not as the new state.
3. **Keep a slow fallback refresh** (for example every 15–30 minutes) so a lost event is never permanent.
4. Pass its last seen `sequence` when reconnecting, so Pylon can replay from there or send `resync`.

## Failure handling

| Failure | Behaviour |
|---|---|
| Postern unavailable | Adapters back off. When Postern is back, Pylon sends `resync` for the affected sources |
| A source is rate-limited | The adapter slows down using Postern's rate-limit headers |
| Pylon restarts | The replay log persists. Consumers reconnect with `since=` and get a replay or a `resync` |
| A consumer is slow | Events are dropped for that consumer, which then gets `resync` |

## Staging

Pylon starts after Postern v1 is done and Perch is a Postern client.

1. **v1:** the GitHub PR and Actions poll adapter, the event contract, the replay log and Perch as a subscriber.
   Perch's own GitHub polling moves into Pylon.
2. Octopus adapter.
3. Graph mail and calendar adapters (once Postern has Graph through WAM).
4. A second real consumer. If none appears, ask whether Pylon should stay a separate product.
5. Push mode through a webhook relay, per source.

## Open questions

- **Approval model.** Pylon's own approval (two prompts per app, but the products stay fully separate) or an
  identity shared with Postern (one prompt, but the products are linked)? The current lean is separate, using the
  same pattern.
- **Webhook signature checks for push mode.** Get the signing secret from Postern's raw-secret tier, or ask
  Postern to verify the signature?
- **Relay choice** for push: Hookdeck, Webhook Relay, ngrok or a self-hosted smee-style server.
- **Replay log storage and retention.** SQLite (like waitbus), how long to keep it, and how many events.
- **Adapter plugin model.** In process for v1. When should adapters move out of process?
- **How big a hint can be** before it becomes data. Decide per source.
