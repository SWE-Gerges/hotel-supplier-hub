# 2. A keyed DI supplier registry instead of a switch

## Context

The orchestrator has to find suppliers by id, know which ones a tenant may use, and treat every supplier the same way. A
`switch` over supplier names, or an enum of suppliers, couples the core to every integration and makes adding one an edit in
several places.

## Decision

- `SupplierId` is a validated lower-kebab string (`supplier-a`), never an enum.
- `services.AddHotelSupplier<TImpl>(SupplierId)` registers the implementation as a keyed singleton and exposes it through
  `ISupplierDescriptor` and through each capability port it actually implements.
- `ISupplierRegistry` is built from those registrations. It fails when a descriptor reports a different id than it was
  registered under, and `AddHotelSupplier` refuses a duplicate id.
- Tenant enablement is configuration (`Hub:Tenancy`). `ITenantSupplierSelector` intersects the tenant's enabled list with the
  registry, with the capability to search and with the request's optional `suppliers` filter. Unknown, disabled or
  non-searching suppliers are skipped, not errors. The tenant's list order is also the supplier priority used when merging.

## Consequences

- Adding a supplier is one `AddHotelSupplier` call plus configuration; the orchestrator contains no supplier names.
- Suppliers are singletons and must be thread-safe.
- Resolution through keyed services is checked when the registry is first built, not at compile time.
- Tests can build a `StaticSupplierRegistry` from stub suppliers without a container.
