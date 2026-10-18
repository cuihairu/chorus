# Admin history dimensions

## Decision

Admin overview history supports one bounded dimension: platform. Supported buckets are `douyin`, `kuaishou`, `bilibili`, `test`, and `other`; values outside the canonical set, including missing platform names, are folded into `other`. Mode and region are intentionally excluded from historical aggregation because their values are deployment-controlled and can grow without bound.

History keeps the existing 720 samples at the five-second sampling interval, for a one-hour window. Each sample stores the existing global totals plus per-platform room and distinct online-user counts. The admin API accepts `GET /api/history?platform=<bucket>`; omitted platform returns the global series. Legacy samples without per-platform fields are omitted from filtered responses rather than shown as zero-valued observations.

This bounds each sample to at most five platform buckets and avoids multiplying storage by arbitrary mode/region values. The overview trend selector exposes the same buckets. Room and user counts are operational summaries; this history does not claim platform audience or engagement totals.

## Accepted event heat

Reliable event heat is counted in the same transaction that stores the event receipt and pending event. Duplicate deliveries do not increment it, and consuming the pending event does not erase the aggregate. Storage uses one-minute buckets by room, canonical platform, and event kind to avoid a single platform-wide write lock; reads roll those rows up by platform and kind. The aggregate stores event count plus raw gift units and is pruned to a rolling 60-minute window. Gift value is deliberately excluded because platform value units are not comparable. The admin hotspot view labels these as accepted ingress events, not applied game actions or total platform audience.

The initial attention rule is 300 accepted events for one platform in one UTC minute. This is an informational console alert, not an external page. Operators with the `operate` permission can acknowledge an alert; acknowledgement state is retained for the same 60-minute window and each first acknowledgement is written to the existing audit log. Acknowledging an expired or sub-threshold bucket is rejected.
