# 4. Canonical hotel ids, merge aliases, and failing open

## Context

Suppliers list the same physical hotel under different codes and names. Results can only be merged if every supplier code maps
to one canonical identity. Identities also change: two canonical hotels turn out to be the same place and one is retired. A
lookup problem in the identity store must not make hotels vanish from a search.

## Decision

- Canonical ids are opaque strings (`H-000123`). The identity store keeps one row per supplier record with a unique index on
  `(supplier, supplier code)`; one canonical id may own many records, up to a configurable cap (default 50). Supplier-specific
  codes are looked up through one dictionary per canonical id (`SupplierCodeMap`), not a column per supplier.
- Retired ids are recorded as aliases pointing at a survivor. `AliasResolver` follows chains for at most five hops with cycle
  detection, caches found aliases for ten minutes and "not an alias" for two (using `TimeProvider`), and leaves the id
  unchanged on a cycle or an over-long chain. The admin endpoint refuses to write an alias that would create a cycle.
- **Fail open.** If the alias store errors, the resolver returns the id unchanged, logs it and counts it in the
  `hub.mapping.alias_lookup_failures` metric. A search that cannot reach the identity store within `IdentityLookupTimeout`
  stops asking for the rest of that search and returns the hotels unmapped.
- The store has an in-memory implementation and an EF Core one (SQLite in tests and for a persistent demo). The same contract
  tests run against all of them.

## Consequences

- Hotels are never lost because of a lookup failure; the price is that results may briefly be unmerged or shown under a retired id.
- Cached aliases can be stale for up to the cache lifetime on other instances. Writing an alias clears the local cache.
- The per-canonical-id cap is checked before insert and is best effort under concurrent adds. The unique index is the hard guarantee.
- The identity schema is created with `EnsureCreated` (SQLite and the in-memory store). The PostgreSQL migrations in
  `Hub.Migrations.Postgres` cover this context too, but have not been applied to a real server in this repository.
