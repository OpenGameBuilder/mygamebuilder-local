# Contributing to MyGameBuilder Local

Thank you for helping with MyGameBuilder Local. This project is a local, offline-compatible backend for the legacy MyGameBuilder client.

Useful contributions include code, tests, documentation, compatibility reports, setup fixes, and safe notes about observed legacy behavior.

## Development

Development setup instructions are in [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md).

Before opening a pull request, run the relevant checks when practical:

```pwsh
dotnet build mygamebuilder-local.slnx
dotnet test mygamebuilder-local.slnx
```

The repo includes `.editorconfig` and a Husky.NET pre-commit hook for formatting staged C# files.

## Compatibility Work

Compatibility contributions should describe behavior, request/response shapes, file formats, or reproducible observations. Do not copy, translate, port, adapt, or mechanically rewrite decompiled source code from the original MyGameBuilder Flash client or any other proprietary source.

Public issues and pull requests should not include passwords, private information, sensitive archival material, security vulnerability details, or decompiled source code.

## Pull Requests

Good pull requests are focused and easy to review. Include a summary, why the change is needed, how it was tested, and any compatibility tradeoffs.

Documentation updates are especially helpful when behavior changes or a new setup step is introduced.
