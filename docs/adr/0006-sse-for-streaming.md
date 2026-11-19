# 6. Server-sent events for streaming results, not a hub

## Context

Clients want to show hotels as suppliers answer instead of waiting for the slowest one. The traffic is one-way: the server
reports progress, the client only starts and cancels a search. A bidirectional hub (WebSockets or a SignalR-style service) adds
a connection protocol, sticky sessions and client libraries for something plain HTTP can already do.

## Decision

- `POST /api/v1/searches/stream` answers with `text/event-stream`. Each `SearchUpdate` is one event: the event name is the
  update type (`supplierStarted`, `hotelsAdded`, `hotelUpdated`, `supplierFailed`, `supplierCompleted`, `completed`), `id` is a
  per-run sequence number (1, 2, 3, ...), and `data` is the update as JSON.
- While the stream is quiet the server sends comment lines (`: heartbeat`) so intermediaries keep the connection open.
- Nothing is written, not even headers, until the first update is known. Refusals raised while a search starts (validation,
  a search id already in use, replacing somebody else's search) are therefore ordinary problem-details responses.
- A client may pick a `searchId` so the search can be cancelled with `DELETE /api/v1/searches/{id}`. The owner check compares
  tenant and caller.
- `POST /api/v1/searches` offers the same search as one JSON response, bounded by `waitMs`.
- Every update of a run is also appended to a bounded per-run event log (`Hub:Streaming:ReplayBufferSize`, default 512; finished
  runs are kept for `ReplayRetention`, default 5 minutes). `GET /api/v1/searches/{id}/events` replays the events after the
  `Last-Event-ID` header and then continues live. An unknown search answers 404 and an id that has fallen out of the buffer
  answers 410, both as problem details.
- A run outlives its last listener for `Hub:Streaming:DisconnectGrace`, so a dropped connection can resume. With a grace of zero
  (the code default) a client that disconnects cancels its own search at once; the demo configuration uses 30 seconds.

## Consequences

- It works through `curl -N`, proxies and plain HTTP clients; browsers need `fetch` streaming because `EventSource` cannot POST.
- Resuming works within the buffer and the retention window only. A listener that falls further behind than the buffer holds is
  cut off and told to reconnect; if the gap can no longer be filled, the reconnect is a 410 and the client starts a new search.
- The buffer is per process: a run and its replay live on one instance.
- Back-pressure comes from the bounded update channel; a stalled reader stalls the search until it is cancelled or times out.
