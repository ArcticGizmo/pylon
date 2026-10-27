# Pylon prior art

Searched on 2026-10-21. Nothing found is a Windows-native local event broker that turns external systems into
change hints for desktop apps with per-app topic access. These come closest.

| Candidate | What it does | Why it doesn't fit as-is | What to take |
|---|---|---|---|
| **waitbus** | A local event bus. GitHub Actions events by webhook with a 45-second ETag polling fallback, a SQLite store, a replay log (`since=`), plugin sources | Linux and macOS only (WSL2 on Windows). **Any process running as the same user can read every event**, with no subscriber auth. 2 stars, one maintainer | The replay log with `since=`, poll plus webhook per source, the plugin entry points |
| **Nango syncs** | Webhooks plus polling for reconciliation. Hashes records so only real changes emit an event, and drops duplicates | Server-side multi-tenant. Syncs and webhooks are Enterprise or cloud only | Hash-based change detection. Webhooks for speed, polling for correctness |
| **Composio triggers** | Webhook triggers where the provider has them, polling (15-minute minimum) otherwise, normalised events | Cloud service | Confirms the hybrid model and the normalised event shape |
| **Octopus subscriptions** | Built-in webhooks and email per event | Needs a public endpoint | A push source once a relay exists |
| **Webhook relays** (Hookdeck CLI, Webhook Relay, ngrok, smee) | Deliver webhooks to localhost over an outbound connection | Transport only | **Use one** for push mode rather than building a relay |
| **Hookdeck Outpost** | Open-source outbound event delivery to webhooks, queues, buses | Server-side fan-out, not local IPC | Delivery and retry ideas |
| **D-Bus** | The Linux desktop IPC bus | Linux. No equivalent on Windows | The publish/subscribe model |

Caveat: some comparisons are vendor-authored (Nango, Webhook Relay, Hookdeck), and smee.io's 2026 status wasn't
confirmed.

## Sources

- [waitbus (GitHub)](https://github.com/astrogilda/waitbus)
- [Nango – Data sync](https://nango.dev/platform/data-sync)
- [Nango – Managed webhooks](https://nango.dev/platform/webhooks)
- [Composio – Triggers docs](https://docs.composio.dev/docs/triggers)
- [Octopus – Subscriptions](https://octopus.com/docs/administration/managing-infrastructure/subscriptions)
- [Hookdeck – Introducing Outpost](https://hookdeck.com/blog/outpost-open-source-webhooks-event-destinations-infrastructure)
- [Webhook Relay – Receive webhooks on localhost](https://webhookrelay.com/blog/receive-webhooks-on-localhost/)
- [Hookdeck – Local tunneling tools](https://hookdeck.com/webhooks/platforms/cloudflare-tunnel-alternatives-for-local-webhook-development)
- [Wikipedia – D-Bus](https://en.wikipedia.org/wiki/D-Bus)
