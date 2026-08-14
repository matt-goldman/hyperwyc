# Issue 18 — Sample: ASP.NET Core Product/Sales API

## Summary

Build the minimal ASP.NET Core Web API that serves as the backend for the Hyperwyc proof-of-concept. This API is the target server for all requests made by the MAUI sample app (issue #19).

## Background

The POC demonstrates all of Hyperwyc's key behaviours in a realistic end-to-end scenario. The backend is a simple notes CRUD API; it is intentionally minimal so the behaviour under test is Hyperwyc's, not the API's.

## Endpoints

Scope changed from a notes CRUD API to a product/sales one, which exercises Hyperwyc better:
reads and writes hit *different* resources, so cache staleness and write replay can be
demonstrated independently.

| Method | Route | Description |
|---|---|---|
| `GET` | `/products` | The catalogue |
| `GET` | `/products/{id}` | A single product |
| `POST` | `/products/regenerate` | Rebuild the catalogue at runtime, for testing stale caches |
| `GET` | `/sales` | Sales recorded so far |
| `POST` | `/sales` | Record a sale; honours `Idempotency-Key`, decrements stock |

## Idempotency Support

- On `POST /sales`, read the `Idempotency-Key` header.
- If a sale was already recorded under that key, return it with `200 OK` rather than recording
  a second one. A new sale returns `201 Created`.
- An in-memory dictionary is sufficient.

## Failure responses worth having

The sample deliberately offers ways to make writes fail, so the client can show the retry and
dead-letter paths:

| Status | Cause |
|---|---|
| `400` | Quantity of zero or less |
| `404` | Unknown product id |
| `409` | Insufficient stock |

## Solution Structure

An Aspire solution under `sample/`:

```
sample/
├── Hyperwyc.Sample.ApiService/   ← this issue
├── Hyperwyc.Sample.AppHost/      ← Aspire orchestration
├── Hyperwyc.Sample.Maui/         ← issue #19
├── Hyperwyc.Sample.ServiceDefaults/
└── Shared/                       ← Product, Sale
```

## Acceptance Criteria

- [x] API project runs with `dotnet run`.
- [x] Catalogue generated randomly at startup — names, prices and stock all vary per run.
- [x] `GET /products`, `GET /products/{id}`, `GET /sales`, `POST /sales` functional.
- [x] `Idempotency-Key` deduplication on `POST /sales`, verified against a running server: a
      repeated key returns the original sale and does not decrement stock twice.
- [x] Recording a sale decrements stock, so a cached catalogue becomes observably stale.
- [x] `POST /products/regenerate` allows the catalogue to change without restarting the API.
- [x] Failure responses available for exercising retry and dead-letter (400/404/409).
- [x] Shared `Product` and `Sale` used by the API.
- [x] `POC.md` updated for the product/sales domain and the Aspire layout.

## Notes

- Persistence is in-memory — no database setup required, and a restart is itself a useful way
  to leave a client holding a stale catalogue.
- The catalogue is generated from name components rather than seeded from a real products API,
  so the sample has no network dependency at startup. That matters for a sample whose whole
  subject is working without a network.
- CORS is not configured: native MAUI clients do not need it. A Blazor client would.
- The dead-letter notification endpoint from the original scope was dropped. Dead-lettering is
  observable in the client through `IHyperwyc.SyncEvents`, which is the surface the sample
  should demonstrate; posting failures back to the API tests nothing about Hyperwyc.
