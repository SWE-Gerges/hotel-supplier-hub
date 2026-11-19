# 3. One resilience pipeline shape for every supplier

## Context

A slow or broken supplier must never delay or break the others, and the rules for that must not depend on which supplier it is.
Special-casing suppliers in the orchestrator ("supplier-b is slow, give it more time") spreads policy through the code.

## Decision

`SupplierGateway` is the only way the orchestrator talks to a supplier. It builds one Polly v8 pipeline per supplier id, from
options (`Hub:Resilience:Defaults`, overridden per supplier, falling back to the supplier's descriptor):

1. **Retry**: only for failures marked `Retryable`, exponential backoff with jitter, a maximum number of attempts.
2. **Circuit breaker**: failure ratio, minimum throughput, sampling and break duration. Transient, timeout, unavailable,
   rate-limited and unknown failures count; bad requests and credentials do not.
3. **Bulkhead**: a concurrency limiter with a bounded wait queue. Waiting is cancellable, and a full queue rejects at once.
4. **Timeout** per attempt, innermost, so a hang is a timeout that the circuit breaker sees.

The in-flight counter that `GET /api/v1/suppliers` reports is incremented inside the bulkhead, so it counts calls that are
running and not callers that are waiting. The circuit state comes from the breaker's state provider. A global search
deadline in the orchestrator bounds the whole run on top of this.

A search is a stream, so one attempt drains the supplier's stream and hands each batch to a callback as it arrives. A retry
restarts the stream, so batches can be delivered again; the merger is idempotent by offer id, and the reported hotel count is
that of the final attempt.

## Consequences

- No supplier-specific code in the orchestrator; behaviour differs only through configuration.
- A hanging supplier costs one timeout per attempt and counts towards opening its circuit.
- Retries after a partial stream re-deliver earlier batches; this is safe because merging ignores identical re-deliveries.
- Circuit and bulkhead state is per process. Several instances each keep their own.
