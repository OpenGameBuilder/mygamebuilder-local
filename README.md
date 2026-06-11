# MyGameBuilder Local

[![CI](https://github.com/OpenGameBuilder/mygamebuilder-local/actions/workflows/ci.yml/badge.svg)](https://github.com/OpenGameBuilder/mygamebuilder-local/actions/workflows/ci.yml)

MyGameBuilder Local is a modern C#/.NET backend for running the legacy MyGameBuilder client locally and offline. It emulates the original Rails and Amazon S3 endpoints closely enough for the Flash client, via Ruffle, to talk to a local ASP.NET Core server at `http://127.0.0.1:3000`.

The project is part of the OpenGameBuilder preservation effort, but this repo is intentionally smaller than the main `opengamebuilder` app: it focuses on a local backend, archive-backed piece storage, and compatibility with the legacy client.

## What Is Included

- ASP.NET Core minimal API backend.
- Wire-compatible XML fragment and SOAP-style endpoints used by the legacy client.
- SQLite archive/overlay storage for MyGameBuilder pieces.
- Built-in fallback profiles for the special `guest` and `!system` accounts.
- xUnit coverage for account, piece store, SOAP, fallback profile, and game-stat behavior.
- Visual Studio and VS Code development setup.

## Getting Started

Install the .NET SDK version from [`global.json`](global.json), then run:

```pwsh
dotnet tool restore
dotnet restore mygamebuilder-local.slnx
dotnet build mygamebuilder-local.slnx
dotnet test mygamebuilder-local.slnx
dotnet run --project src/MyGameBuilder.Local.Api
```

Open `http://127.0.0.1:3000` after the server starts.

For editor-specific setup, see [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md).

## Documentation

- [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md) - local setup for CLI, Visual Studio, and VS Code.
- [`docs/RELEASES.md`](docs/RELEASES.md) - release workflow and patch-release process.

## Contributing

Issues and pull requests are welcome. Please read [`CONTRIBUTING.md`](CONTRIBUTING.md) before submitting compatibility or archival work, and report security issues privately as described in [`SECURITY.md`](SECURITY.md).
