# 7. Quotes, the shown-equals-paid invariant, and idempotent bookings with an Unknown state

## Context

A search price is a cached supplier price. Between the search and the booking the supplier's price can move, the client can
retry a request that timed out, two requests can race, and a supplier can accept a booking and then lose its answer. Booking
twice, or charging an amount the client never saw, costs real money and trust.

## Decision

- **Quote first.** `POST /api/v1/quotes` re-prices the offer with its supplier (prebook, or a fresh availability call for a
  supplier without prebook) and records a quote: the selling amount, until when it holds, and the cancellation policy. A moved
  price is returned as `priceChanged: {from, to}` and written back to the stored offer, so later reads show it. A newer quote of
  the same offer supersedes older ones.
- **Shown equals paid.** A booking is accepted only against a live quote and the exact amount that quote showed. A different
  amount, a superseded quote, an expired quote or a quote already used is a 409 that carries the current quote. The supplier is
  asked to book at the supplier price the quote carried and refuses if its price moved since, which fails the booking without
  charging anything. A seeded random test drives quotes, price moves, time and booking attempts and checks the invariant.
- **Idempotency in the database.** `Idempotency-Key` is required. The booking row (unique on tenant and key) and the move of the
  quote from active to used are written in one transaction, so a race has exactly one winner. The same key and payload returns
  the stored outcome with `Idempotent-Replayed: true`; the same key with another payload is a 422. The key also derives the
  reference sent to suppliers that refuse to book one reference twice.
- **Unknown is a state.** If the request may have reached the supplier (a timeout or an unreadable answer) the booking is
  `Unknown`, not `Failed`. It is settled by asking the supplier what it holds under the hub's reference; only if the supplier
  proves it holds nothing, and can de-duplicate by reference, is the booking sent again, within a bounded number of attempts. A
  supplier that cannot look bookings up leaves the booking `Unknown` for a person; it is never booked again blindly.
- Asynchronous suppliers answer "accepted"; the booking stays `Pending` until a sync (an endpoint, plus a background sweep)
  sees it confirmed or failed.
- **Pay later** is allowed only for a known free-cancellation deadline at least a configured lead time away. A background
  service, driven by `TimeProvider`, cancels still-unpaid bookings a configured buffer before that window closes.

## Consequences

- Clients make one more call (quote) before booking, and must handle 409 by showing the new price.
- Quotes and bookings are persisted (EF Core: SQLite for the demo and tests, PostgreSQL migrations committed). Offers from a
  search are kept in memory and are lost on restart; a quote on a lost offer is a 404 and the client searches again.
- Booking calls go through the same per-supplier concurrency limit and time budget as searches but are never retried by the
  gateway: whether a lost answer means the call went through is settled by reconciliation, not by a retry policy.
- Several instances need care: the sweep and the per-booking locks are per process, and the booking row carries a version token
  so a lost update fails instead of overwriting.
