# 10. A same-place guard for localised overlays

## Context

A supplier can send a localised name and address for a hotel. Applied blindly, a wrong overlay (another hotel's data under the
wrong code, or a different property with a similar name) renames a hotel for every client, and the mistake survives in the
merged result.

## Decision

- An overlay is data attached to a supplier's hotel. The merger applies it only if it is the same place as the hotel the hub
  already knows: the great-circle distance between the coordinates is within a limit (default 300 m) and the normalised names
  are alike enough (default 0.5; localised names differ in wording, so the coordinates carry the weight).
- When either side has no coordinates the distance cannot be checked, so the names alone must be much closer (default 0.85).
- Suppliers are tried in priority order; the first acceptable overlay wins. A rejected overlay leaves the hotel's own name and
  address alone, is counted (`hub.overlay.rejected`, by supplier) and logged once per hotel and supplier.
- The decision is a pure function of the batches, so the merged result does not depend on which supplier answers first.

## Consequences

- A correct overlay with distant coordinates is rejected; that is the safe side of the trade, and the limits are configuration.
- Only the name and address are overlaid; coordinates, stars and offers always come from the supplier that owns the static data.
