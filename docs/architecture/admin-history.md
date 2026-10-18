# Admin history dimensions

## Decision

Admin overview history supports one bounded dimension: platform. Supported buckets are `douyin`, `kuaishou`, `bilibili`, `test`, and `other`; values outside the canonical set, including missing platform names, are folded into `other`. Mode and region are intentionally excluded from historical aggregation because their values are deployment-controlled and can grow without bound.

History keeps the existing 720 samples at the five-second sampling interval, for a one-hour window. Each sample stores the existing global totals plus per-platform room and distinct online-user counts. The admin API accepts `GET /api/history?platform=<bucket>`; omitted platform returns the global series. Legacy samples without per-platform fields are omitted from filtered responses rather than shown as zero-valued observations.

This bounds each sample to at most five platform buckets and avoids multiplying storage by arbitrary mode/region values. The overview trend selector exposes the same buckets. Room and user counts are operational summaries; this history does not claim platform audience or engagement totals.
