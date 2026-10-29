# Lifecycle flows

These diagrams describe the agreed target lifecycle, not a claim that all paths are implemented. The player view means ordinary live-stream viewers; a directly connected guest/player requires its own authenticated session flow.

## Streamer client launch lifecycle

![Streamer client launch lifecycle](/images/flow-launch-en.svg)

[SVG](/images/flow-launch-en.svg)

Platform evidence rechecked on 2026-10-29: [Douyin live-info API](https://developer.open-douyin.com/docs/resource/zh-CN/interaction/develop/server/live-room-scope/audience-play/get-live-info) documents companion / cloud launch tokens passed to the backend. [Kuaishou gameplay capabilities](https://open.kuaishou.com/miniPlayDocs/technologyrelated/Affiliatedability/basicability/localdevelopment.html) requires server-side binding after client launch before interaction delivery. These verify credential/binding steps, not identical launcher UI or packaging across platforms.

Handle invalid/expired credentials before LoginReady; never log secrets. Duplicate launch must resolve the current session/round, not create another room. A resumed client uses Chorus credentials. Platform binding may start callbacks before matching; classify pre-round events explicitly. Client close, live end and network loss are different causes.

## Lifecycle of one gift event

![Lifecycle of one gift event](/images/flow-gift-en.svg)

[SVG](/images/flow-gift-en.svg)

Failure rules: reject forged signatures; without a valid room route, retain or reject according to the declared policy. StaleOwner refreshes the owner of the same room. Unknown delivery retries the same EventId. Duplicates return the original acceptance. Late gifts retain the fixed round attribution and receive an explicit terminal disposition. Only gift capabilities with proven stable IDs may claim reliable deduplication. Gameplay reward handling does not imply payment refunds.

## Lifecycle of a like event

![Lifecycle of a like event](/images/flow-like-en.svg)

[SVG](/images/flow-like-en.svg)

Like counts depend on each platform capability: a tap may be absent, delayed or batched upstream. Record the accepted count, merged count, applied count and any configured best-effort drops separately. Do not invent event IDs or deduplicate identical counts by payload hash. Durable inputs must not silently become best effort under load.

## Complete room lifecycle

This operational view separates room creation, route registration and installation ACK, battle activation, repeated state updates, route closure and room destruction. A prepared RoomActor must not start ticking before the route barrier completes. This is the target contract; the existing PrepareRoom implementation currently activates too early and needs migration.

![Complete room lifecycle](/images/lifecycle-room-en.svg)

[SVG](/images/lifecycle-room-en.svg)

## Streamer lifecycle

A streamer survives connections and individual rounds.

![Streamer lifecycle](/images/lifecycle-streamer-en.svg)

Open full SVG：[SVG](/images/lifecycle-streamer-en.svg)

## Player / audience lifecycle

Baseline: viewers interact through the live platform, without a Chorus connection.

![Player / audience lifecycle](/images/lifecycle-player-en.svg)

Open full SVG：[SVG](/images/lifecycle-player-en.svg)

## Live-platform integration lifecycle

Platform-specific callbacks and close operations obey the adapter capability contract.

![Live-platform integration lifecycle](/images/lifecycle-platform-en.svg)

Open full SVG：[SVG](/images/lifecycle-platform-en.svg)

The room-end barrier is mandatory: freeze the result → close delivery and confirm all authorized gateways → authorize the platform call → resolve its outcome and complete settlement → finalize and destroy. A failed or unknown platform outcome blocks the next round. Normal round completion must not implicitly unbind the entire live session.

See [architecture and shutdown contract (Chinese)](/architecture/architecture).

[Current GameServer threading and message flow](/design/gameserver-threading-current) is documented separately from these target lifecycles.
