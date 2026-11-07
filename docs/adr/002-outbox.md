# 002 — Transactional outbox and deduplicating inbox

Accepted. SQL and Service Bus cannot atomically commit together without a distributed transaction. Persist the event alongside the transfer, then publish asynchronously using a stable event ID. A crash after send and before acknowledgement persistence causes a duplicate. Consumer inbox primary keys and a transaction-owned event lock make the local SQL projection idempotent.

Use one serialized dispatcher with bounded batches, publish timeouts and terminal retry state. This deliberately trades throughput for simple recovery. Future scale can use expiring row leases and partitioned workers. Preserve failed rows for diagnosis and explicit replay; do not repeat the monetary operation to fix notification delivery.

The subscription-free local transport uses the same inbox consumer. It does not establish real Azure transport behavior. Azure mode uses a queue and explicit completion after inbox persistence; delivery and ordering claims remain at-least-once and unordered.
