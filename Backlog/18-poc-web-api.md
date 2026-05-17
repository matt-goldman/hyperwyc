# Issue 18 — POC: ASP.NET Core Web API Backend

## Summary

Build the minimal ASP.NET Core Web API that serves as the backend for the Hyperwyc proof-of-concept. This API is the target server for all requests made by the MAUI sample app (issue #19).

## Background

The POC demonstrates all of Hyperwyc's key behaviours in a realistic end-to-end scenario. The backend is a simple notes CRUD API; it is intentionally minimal so the behaviour under test is Hyperwyc's, not the API's.

## Endpoints

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/notes` | Return all notes |
| `POST` | `/api/notes` | Create a note; respects `Idempotency-Key` header for deduplication |
| `PUT` | `/api/notes/{id}` | Update a note |
| `DELETE` | `/api/notes/{id}` | Delete a note |
| `POST` | `/api/deadletter` | Receive dead-letter notifications (optional) |

## Idempotency Support

- On `POST /api/notes`, read the `Idempotency-Key` header.
- If a note was already created with this key, return the existing note with `200 OK` instead of creating a duplicate.
- A simple in-memory dictionary is sufficient for the POC.

## Solution Structure

```
Hyperwyc.Sample/
├── WebApi/           ← this issue
├── MauiApp/          ← issue #19
└── Shared/           ← DTOs (NoteDto, etc.)
```

## Acceptance Criteria

- [ ] `Hyperwyc.Sample/WebApi` project created and runs with `dotnet run`.
- [ ] All four CRUD endpoints functional.
- [ ] `Idempotency-Key` deduplication implemented on `POST /api/notes`.
- [ ] Shared `NoteDto` in `Hyperwyc.Sample/Shared`.
- [ ] CORS configured for local MAUI/desktop clients.
- [ ] `README` or inline comments document how to run the API alongside the MAUI app.

## Notes

- Persistence is in-memory (`List<Note>`) — no database setup required.
- The `POST /api/deadletter` endpoint can be a stub that logs received payloads.
