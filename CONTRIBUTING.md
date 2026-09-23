# Contributing

Compositor for Windows is an independent C#/Avalonia rewrite. Contributions are welcome while the application is in alpha.

## Before changing code

- Read the [live port status](docs/port-status.md) and relevant section of the [port plan](docs/windows-port-plan.md).
- Treat [robbietilton/Compositor](https://github.com/robbietilton/Compositor) as reference material, not a Git upstream to merge. Reimplement behavior for the Windows architecture.
- Preserve unrelated working-tree changes and keep commits focused.
- Do not commit `dist/`, build outputs, diagnostic logs, test documents, or third-party images.

## Build and verify

Requirements are the .NET 10 SDK and Visual Studio 2022's **C++ Clang tools for Windows** component.

```powershell
dotnet restore Compositor.slnx
.\scripts\verify.ps1
```

For a self-contained local package and smoke test:

```powershell
.\scripts\verify.ps1 -Publish
```

Changes must build without warnings. Add focused tests for persistence, destructive operations, invariants and regressions. UI/tool work should arrive as a usable vertical slice rather than disconnected infrastructure.

## Reporting bugs

Follow [docs/alpha-testing.md](docs/alpha-testing.md). Include the shortest reproduction, expected and actual behavior, relevant input/project files when shareable, and the newest log from **Help → Open Diagnostic Logs**. Logs include local file paths, so review them before posting publicly.

## Pull requests

- Explain the user-visible outcome and any deliberate limitations.
- List automated and manual verification performed.
- Call out project-format changes, compatibility risks, or operations that can alter user data.
- Do not create release tags or upload alpha artifacts as releases unless the maintainer explicitly requests it.
