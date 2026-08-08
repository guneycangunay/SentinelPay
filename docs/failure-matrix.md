# Payment failure-mode matrix

This matrix records the expected durable state, client behavior, automatic recovery, and operator evidence for the failure paths exercised by SentinelPay.

| Boundary and failure | Durable local state | Client/provider behavior | Recovery path | Evidence |
|---|---|---|---|---|
| Invalid merchant credential | No mutation | `401` | Issue or restore valid credential | Authentication warning; no payment row |
| Same key, changed request | Original resource only | `409` | Send the original request or a new key | Stored request hash and conflict log |
| API stops before operation commit | No remote call is permitted | Client sees disconnect | Retry starts normally | No operation row |
| API stops after `Started`, before provider call | Payment plus `Started` operation | Client retries same key | Resume provider call | Operation timestamps |
| Provider accepts, response is lost | `Started`; provider may have changed | API returns retryable `503` | Same-key retry or status reconciliation | Stable operation and provider key |
| Provider returns business decline | Failed payment and failed operation | Final payment response | No transport retry | Provider code, outbox failure event |
| Provider returns `429` | `Started` until final result | Honor short `Retry-After`; same-key bounded retry | Adapter retry, then client retry if exhausted | Provider latency and request trace |
| Provider returns repeated `5xx` | `Started` | `503 retryable` | Client retry or reconciliation | Provider error and started-operation age |
| 3DS challenge times out | `RequiresAction` until expiry worker closes it | Merchant must not capture | Expiry transition and event | `ActionExpiresAt`, expiry operation |
| Authentication callback is delivered twice | First transition plus webhook receipt | Second delivery acknowledged | Inbox replay | `(provider,eventId)` unique receipt |
| Two partial captures exceed remainder | At most winning capture commits | Losing request receives `409`/`422` | Read current payment, recalculate remainder | Capture rows, operation rows, row version |
| Capture succeeds, final DB commit is lost | Capture operation remains `Started` | Same provider key is replayed | Provider returns original capture reference | Provider key and deterministic local capture ID |
| Authorization expires after partial capture | Captured amount retained; remainder closed | No further capture | Expiry worker emits close event | Capture journal plus expiry operation |
| Refund exceeds captured balance | No provider call | `422` | Correct amount | Aggregate validation log |
| RabbitMQ unavailable | Business state and outbox remain committed | Payment API can complete | Outbox backoff and reclaim | Pending age, attempts, error metric |
| Publisher confirms, process stops before `ProcessedAt` | Outbox row still pending | Event may be delivered again | Stable CloudEvent ID | Outbox ID and broker message ID |
| Consumer commits, ACK is lost | Consumer inbox side effect committed | Broker redelivers | Duplicate is ACKed without another insert | `(consumer,eventId)` unique row |
| Consumer receives malformed JSON | No application side effect | Delivery rejected, no requeue | Inspect/fix producer, controlled replay | Audit DLQ and warning log |
| Consumer database is unavailable | No ACK | Delivery is requeued | Retry after database recovery | Consumer error log and queue depth |
| Provider CSV imported twice | Original report returned | No duplicate issue set | Hash-based report replay | Source SHA-256 unique key |
| Provider row missing locally | Review issue only | No silent payment creation | Investigate provider/merchant identity | `MissingLocally` issue |
| Local payment missing in provider report | Review issue only | No silent cancellation | Validate report window and provider status | `MissingAtProvider` issue |
| Currency or amount differs | Review issue only | No automated monetary rewrite | Financial operations review | Typed issue with both values |
| Online reconciliation item fails | Prior items remain committed | Remaining batch continues | Retry next cycle | Per-payment warning |

## Rules behind the matrix

- Unknown remote outcome is not converted into failure or success by assumption.
- Transport errors and business results have different retry policies.
- Financial repair is forward-only and narrow; broad report mismatches are review workflows.
- A broker acknowledgement always follows the database commit that owns the side effect.
- Every replay boundary has a stable identity and a database uniqueness constraint.
