# 001 — SQL transactions with ordered application locks

Accepted. SQL Server is the single consistency boundary. Read Committed plus transaction-owned `sp_getapplock` locks serializes spending and idempotency across processes. Acquire the caller/key lock first, then account GUID locks in sorted order; all money writers participate. Keep ledger, balances, idempotency and outbox in one commit. Unique keys and nonnegative balance checks add database guards.

This is simpler to explain and test than optimistic retries on two independently versioned account rows. It requires every writer to follow the protocol and does not protect against privileged manual SQL. Deadlocks retry the entire unit of work at most three times. Lock timeouts and uncertain network outcomes are resolved by client retries with the same durable idempotency key.

Subjects and keys use binary collation so database uniqueness agrees with case-sensitive lock resource identities. Entries are authoritative history; balances are a transactional materialization. Development funding uses a synthetic clearing account and balanced entries. No production deposit mechanism is implied.
