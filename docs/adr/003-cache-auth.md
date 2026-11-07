# 003 — Optional metadata cache and externally validated ownership

Accepted. Redis caches only account identity/currency/closed metadata for 60 seconds, scoped by owner and ID. No cache lookup determines spending, write authorization or the balance response. Cache failures fall back to SQL. There is no public metadata mutation; a future mutation must invalidate matching keys.

JWT subjects establish ownership. A configured HTTPS authority validates external JWTs. Development token minting requires both Development environment and an explicit opt-in, with an ephemeral signing key. Development funding is a separate authenticated, environment-gated endpoint. These mechanisms are for synthetic local use and are not production identity or funding solutions.
