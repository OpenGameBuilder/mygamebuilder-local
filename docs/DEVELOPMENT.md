# Developer Setup

This repo builds a local ASP.NET Core backend for the legacy MyGameBuilder client. It is intended to run at `http://127.0.0.1:3000`, because the client and URL rewrite rules expect that origin.

## Requirements

- [.NET SDK 10.0.300+](https://dotnet.microsoft.com/download), matching [`global.json`](../global.json).
- [Visual Studio 2026+](https://visualstudio.microsoft.com/vs/) with the ASP.NET and web development workload, or [Visual Studio Code](https://code.visualstudio.com/) with the recommended extensions.
- Git for Windows if you want the Husky.NET pre-commit hook to run locally.

Visual Studio can install the required workload from [`../.vsconfig`](../.vsconfig) when you open the solution.

## Command Line

From the repository root:

```pwsh
dotnet tool restore
dotnet restore mygamebuilder-local.slnx
dotnet build mygamebuilder-local.slnx
dotnet test mygamebuilder-local.slnx
dotnet run --project src/MyGameBuilder.Local.Api
```

Then open `http://127.0.0.1:3000`.

## Visual Studio

1. Open [`../mygamebuilder-local.slnx`](../mygamebuilder-local.slnx).
2. Let Visual Studio install missing components from [`../.vsconfig`](../.vsconfig) if prompted.
3. Set `MyGameBuilder.Local.Api` as the startup project.
4. Press F5. The project launch profile binds to `http://127.0.0.1:3000`.

## VS Code

1. Open the repository folder in VS Code.
2. Install the recommended extensions from [`../.vscode/extensions.json`](../.vscode/extensions.json).
3. Press F5 and choose `Launch Local Backend`.

Useful tasks are available from **Terminal: Run Task**:

- `build-api`
- `build-all`
- `test`
- `watch-api`
- `clean`

## Local Data

The default configuration uses:

- `archive.sqlite` for imported read-only archive content.
- `overlay.sqlite` for writable local overlay data.
- hard-coded in-memory fallback profiles for the special `guest` and `!system` accounts when no archive/overlay profile exists.

The archive file is optional; when it is absent, the app behaves as an empty base archive. The app creates `overlay.sqlite` on startup if it is missing. Overlay objects and tombstones are stored there after the client writes or deletes pieces.
