# 5. Merge locking, and merge options that belong to a run

## Context

Several supplier tasks merge batches into one result at the same time, and the same hotel often arrives from two suppliers at
once. The result must not depend on arrival order, updates for one hotel must reach the client in the order they happened,
and two searches running side by side must not influence each other. Process-wide switches ("mapping on/off", "max offers")
make concurrent runs with different settings impossible to reason about.

## Decision

- `HotelMerger` keeps, per hotel key, the contribution of each supplier. The merged hotel is recomputed as a pure function of
  those contributions: static data from the highest-priority supplier, offers sorted by selling price, then supplier priority,
  then offer id, sources ordered by priority. Re-delivering an identical offer changes nothing and reports nothing.
- A hotel is keyed by its canonical id, or by `(supplier, code)` when it has none, so duplicates within one supplier collapse.
- Concurrent batches are serialised per key with `KeyedAsyncLock`. When a batch touches several hotels, the keys are taken in
  one global ordinal order, so callers that need the same keys in opposite order cannot deadlock. Entries are reference
  counted and removed when idle, and a cancelled waiter releases what it already holds.
- Updates are handed to the output channel while the hotel's lock is still held, so one hotel's events keep their order.
- `MergeOptions` (mapping on or off, offers per hotel, price-change threshold) are a value passed into each run. There are no
  static or shared mutable flags.

## Consequences

- A property-style test (seeded random batches, random interleavings) checks that concurrent merging equals a sequential
  reference merge, and another that two concurrent runs with different options do not interfere.
- A slow consumer applies back-pressure while hotel locks are held. Cancellation, the deadline and disposing the stream bound that wait.
- Every offer of a hotel is kept until the final sort, so memory grows with what suppliers return, not with the per-hotel limit.
