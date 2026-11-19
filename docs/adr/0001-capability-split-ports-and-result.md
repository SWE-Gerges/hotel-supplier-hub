# 1. Capability-split provider ports and a single `Result<T>` convention

## Context

Hotel suppliers differ in protocol (paged JSON, token polling, two-step XML), in what they can do (some cannot cancel or
retrieve a booking) and in how they fail (error payloads, HTTP-style codes, malformed bodies, hangs). One wide interface that
every supplier must implement forces stubs that throw `NotSupportedException`. Letting each supplier choose between returning
`null`, a tuple or an exception makes failure handling in the orchestrator a pile of special cases.

## Decision

- Each capability is its own small port: `IHotelSearch`, `IRoomAvailability`, `IPreBook`, `IBook`, `ICancel`, `IRetrieve`.
  A supplier implements only what it can do and says so in `ISupplierDescriptor.Capabilities`.
- Every port returns `Result<T>`: a value or a `SupplierError` with a `Kind` (`Unavailable`, `InvalidRequest`, `Auth`,
  `RateLimited`, `Timeout`, `Transient`, `Unsupported`, `Unknown`), a message, a `Retryable` flag and optional raw detail.
- Suppliers never return null or tuples and never throw for expected failures. Anything unexpected is caught at the supplier
  boundary and converted to `Unknown`. Cancellation is the one exception that escapes, as `OperationCanceledException`.
- Search streams batches as `IAsyncEnumerable<Result<IReadOnlyList<Hotel>>>`. A failure item ends the stream.
- Each supplier owns an `ISupplierResponseNormalizer<TResponse>` from its wire model to the canonical model. Canonical types
  live in `Hub.Abstractions`, never in a supplier namespace.

## Consequences

- The orchestrator and the resilience layer reason about one error vocabulary; retry looks only at `Retryable`.
- A new supplier needs no changes outside its own project and one registration.
- The same contract tests run against all three fake suppliers, which keeps different protocols behind one port.
- The booking-side ports (availability, prebook, book, cancel, retrieve) and the city directory are implemented by the fakes in
  deliberately different combinations: supplier-b has no prebook and books asynchronously, supplier-c needs its search session and
  cannot retrieve. A `DuplicateDetection` flag says which suppliers refuse to book one reference twice; the booking flow relies on it.
- `Result<T>` is a struct whose default value is a success; code that stores it in arrays should initialise entries.
