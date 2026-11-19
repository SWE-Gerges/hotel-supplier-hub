# 9. City crosswalk: fuzzy matching with a review queue

## Context

Suppliers name and code cities differently: other codes, spelling variants, diacritics, abbreviations ("St."), split words,
region names, and the same name in different countries. A static city map does not scale past a demo, and a fully automatic fuzzy
match will sooner or later send guests to the wrong city.

## Decision

- Each supplier publishes a city and country list. A matcher maps a supplier city to a canonical city in three steps: a country
  gate first (an alias table of codes and names per canonical country, so only canonical cities of that country are
  candidates), then a name score (a soft token-set similarity and an edit-distance similarity, with a small bonus for a matching
  region), then a decision.
- A match is accepted automatically only if its score reaches a threshold **and** it beats the runner-up by a margin. Everything
  else, including near-identical names for different cities, an unknown country and a typo with no corroboration, goes to a
  review list with the candidates, the scores and the reason.
- A supplier has at most one code per canonical city, and a code means one city. The matcher resolves clashes inside a run (the
  better match wins, the other is queued); two unique indexes in the database back it when runs overlap.
- A run is idempotent: mapped cities and cities already in the review list (pending, approved or rejected) are left alone. People
  decide through `approve` and `reject`; a rejection sticks.
- Searches resolve supplier city codes through the crosswalk. A supplier with no code for the destination is skipped with a
  clear `supplierFailed` outcome. The API runs the crosswalk once at start-up so the demo cities work at once.

## Consequences

- Name folding uses a table, not Unicode normalisation, because the host runs with invariant globalization.
- The thresholds (score 0.90, margin 0.10) are starting points tuned on the fake data; real data needs its own golden pairs.
- The crosswalk has its own database so it can be reset or moved without touching bookings.
