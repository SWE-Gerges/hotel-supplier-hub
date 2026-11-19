# Benchmark results

Produced by `dotnet run -c Release --project bench/Hub.Bench`. The three fake suppliers run in the same process, so the numbers show
how latency adds up when suppliers are asked one after another and how much of it fan-out hides. They say nothing about real suppliers.

- Run: 2026-11-19 21:18 UTC, 10 measured iterations after 2 warm-up iterations per scenario.
- Machine: 12 logical processors, Microsoft Windows 10.0.26300, .NET 10.0.15.
- Search: one city, 3 nights, 1 room with 2 adults. Sequential asks supplier-a, then supplier-b, then supplier-c through the same resilience gateway and does nothing with the answers. Fan-out is the whole orchestrator: it also resolves city codes, prices every offer, normalises every cancellation policy, stores the offers and merges the hotels.
- Latency is injected on every supplier call (a search makes several calls per supplier), uniformly between the two bounds.
- Results depend on the machine and on what else was running; treat them as one sample, not a guarantee.

| Scenario | Sequential median (ms) | Sequential p95 (ms) | Fan-out median (ms) | Fan-out p95 (ms) | Median speed-up |
| --- | ---: | ---: | ---: | ---: | ---: |
| no injected latency | 128 | 170 | 244 | 311 | 0.5x |
| 40-80 ms per supplier call | 779 | 823 | 478 | 516 | 1.6x |
| 100-200 ms per supplier call | 1720 | 1783 | 833 | 903 | 2.1x |

Reading the table: with no injected latency the fan-out row is the slower one, because it does all of the hub's own work on the
answers (pricing, policy normalisation, merging) while the sequential row only waits for the suppliers; supplier-b alone also needs
about 60 ms by itself (its configured poll interval and batch delay). With latency on every call the sequential path pays for all
three suppliers one after another, while fan-out pays about as much as the slowest supplier plus the hub's own work.
