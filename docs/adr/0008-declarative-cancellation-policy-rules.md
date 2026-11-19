# 8. Cancellation policies from declarative rules, failing safe

## Context

Each supplier describes cancellation differently: free text in several phrasings and date formats, penalty codes, or structured
tiers. Pay-later protection and refund promises depend on reading them correctly, and a wrong "refundable" is much worse than a
missed one. Parsing code buried in each supplier adapter is hard to review and easy to get subtly wrong.

## Decision

- Supplier normalisers only pass the raw policy through (`RoomOffer.Extra`). One normaliser in the core turns it into canonical
  `CancellationTiers` (from, and a penalty in percent, nights or a fixed amount), `RefundableUntil`, `Refundable` and
  `PolicyKnown`. It is the only writer of those fields.
- The mapping is data: one JSON file per supplier (text patterns with date formats and rewrites, a code table, or delimited
  tiers). Files are loaded and checked at startup: unknown fields, bad regular expressions or date formats, unknown groups,
  invalid penalties and duplicate suppliers all stop the host. Rule files can be added from a directory without a build.
- Dates are read as midnight UTC and "until D" means free until the start of D: the earlier reading, so the hub never promises
  more free time than the supplier might.
- **Fail safe.** Text that matches no rule, a date that does not parse, tiers that get cheaper over time, a regular expression
  that times out, or a supplier without rules is an unknown policy: not refundable, no tiers, counted in `hub.policy.unknown`
  and warned about once per supplier. The hub never claims refundable unless it proved it.
- Every known schedule starts when it is read, never gets cheaper, and covers the stay up to check-in.

## Consequences

- Supporting a new phrasing is a rule-file change with a golden-table test, not a code change.
- Regular expressions in data are powerful and easy to get wrong; the checks at startup and the golden tables are what keep that
  safe, and a pathological expression is bounded by a match timeout.
- Anything the rules cannot express (for example a policy that depends on the room's price) needs a new strategy in the engine.
