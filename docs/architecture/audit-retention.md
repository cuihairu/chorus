# Control audit retention

The control audit is append-only. The service does not delete or rewrite audit rows, because the repository has no approved legal or business retention period. This is the initial retention decision (proxy-decided): keep records in the configured control database until an explicit retention and archive requirement is approved.

The admin audit page exposes row count, oldest/newest record timestamps, and an estimated payload size. The estimate uses four bytes per stored text character plus 32 bytes per row; it is intentionally conservative for UTF-8 text, but excludes database pages, indexes, WAL, and backups. At 256 MiB the UI raises a capacity warning. This is an operator warning only; it does not page or delete data.

No archive destination or automated archival job is configured. Operators must provision database capacity and backups before the warning threshold is approached. If policy later requires finite retention, implement a separately reviewed export/verification process before any deletion path; do not add silent cleanup to maintenance.
