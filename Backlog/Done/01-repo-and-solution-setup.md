# Issue 01 — Repository & Solution Setup

## Summary

Create the repository, solution file, and project structure for hyperwyc. This establishes the foundation all other issues build on.

## Background

hyperwyc ships as two NuGet packages:
- **`hyperwyc`** — core package with interfaces, handler, and in-memory store. No storage dependency.
- **`hyperwyc.Cabinet`** — storage provider backed by Cabinet. Depends on `hyperwyc`.

A sample solution (`hyperwyc.Sample`) is also needed for the POC but is tracked separately.

## Acceptance Criteria

- [x] Solution file `hyperwyc.sln` exists at the repo root.
- [x] `src/hyperwyc/hyperwyc.csproj` — class library targeting `netstandard2.1` and/or `net10.0` (TBD).
- [x] `src/hyperwyc.Cabinet/hyperwyc.Cabinet.csproj` — class library; project reference to `hyperwyc`.
- [x] `tests/hyperwyc.Tests/hyperwyc.Tests.csproj` — xUnit test project; project reference to `hyperwyc`.
- [x] `tests/hyperwyc.Cabinet.Tests/hyperwyc.Cabinet.Tests.csproj` — xUnit test project; project reference to `hyperwyc.Cabinet`.
- [x] `.editorconfig` and `Directory.Build.props` for consistent build settings.
- [x] `.gitignore` appropriate for a .NET solution.
- [x] `README.md` at the repo root (can start as a copy of the design-doc version).
- [x] CI pipeline (GitHub Actions) that restores, builds, and runs tests on push.

## Notes

- Target framework choices should support .NET MAUI, Blazor WASM, and plain ASP.NET Core where practical.
- The `hyperwyc` package must have **no** transitive dependency on Cabinet or any storage library.
