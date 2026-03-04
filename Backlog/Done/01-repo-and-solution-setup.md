# Issue 01 — Repository & Solution Setup

## Summary

Create the repository, solution file, and project structure for Restyc. This establishes the foundation all other issues build on.

## Background

Restyc ships as two NuGet packages:
- **`Restyc`** — core package with interfaces, handler, and in-memory store. No storage dependency.
- **`Restyc.Cabinet`** — storage provider backed by Cabinet. Depends on `Restyc`.

A sample solution (`Restyc.Sample`) is also needed for the POC but is tracked separately.

## Acceptance Criteria

- [x] Solution file `Restyc.sln` exists at the repo root.
- [x] `src/Restyc/Restyc.csproj` — class library targeting `netstandard2.1` and/or `net10.0` (TBD).
- [x] `src/Restyc.Cabinet/Restyc.Cabinet.csproj` — class library; project reference to `Restyc`.
- [x] `tests/Restyc.Tests/Restyc.Tests.csproj` — xUnit test project; project reference to `Restyc`.
- [x] `tests/Restyc.Cabinet.Tests/Restyc.Cabinet.Tests.csproj` — xUnit test project; project reference to `Restyc.Cabinet`.
- [x] `.editorconfig` and `Directory.Build.props` for consistent build settings.
- [x] `.gitignore` appropriate for a .NET solution.
- [x] `README.md` at the repo root (can start as a copy of the design-doc version).
- [x] CI pipeline (GitHub Actions) that restores, builds, and runs tests on push.

## Notes

- Target framework choices should support .NET MAUI, Blazor WASM, and plain ASP.NET Core where practical.
- The `Restyc` package must have **no** transitive dependency on Cabinet or any storage library.
