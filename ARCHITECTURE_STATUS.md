# Architecture Status

_Last updated: 2026-11-21_

| Area | Status | Document |
|---|---|---|
| Package structure and dependencies | ACCEPTED, implemented | ADR 0001 (D1 amended: net8/9/10/11; D7 package split) |
| Public API (registration, enqueue, delivery, admin) | IMPLEMENTED | `docs/architecture.md`. Differences from the original proposal are tabulated there |
| State machine | IMPLEMENTED, revised | Shutdown-interrupted and abandoned deliveries keep their attempt (CR-05, CR-06) |
| Delivery guarantees | IMPLEMENTED, tested | `docs/reliability.md` (guarantee → test traceability) |
| Dispatcher | IMPLEMENTED, reviewed | Handler abandonment, claim not cancelled by stopping token, ambiguous-ack re-read |
| Retry policy | IMPLEMENTED | Exponential backoff with equal jitter, measured on the DB clock |
| PostgreSQL schema + claim SQL | IMPLEMENTED, plan-verified | Single statement, `MATERIALIZED` CTE + `SKIP LOCKED`; statistics on partial indexes (CR-03) |
| SQL Server schema + claim SQL | IMPLEMENTED, plan-verified | ADR 0002: two-phase claim |
| Observability | IMPLEMENTED | ActivitySource/Meter "DbRelay"; health check; `dbrelay.delivery.abandoned` added |
| Storage SPI evolution | **OPEN, decision needed before 1.0** | CR-24 / K14: default interface members vs abstract base class |
