# Compositor for Windows

[![License: MIT](https://img.shields.io/github/license/Revan67/Compositor-Windows)](LICENSE)
[![Windows build](https://img.shields.io/github/actions/workflow/status/Revan67/Compositor-Windows/windows.yml?branch=main&label=build&logo=githubactions&logoColor=white)](https://github.com/Revan67/Compositor-Windows/actions/workflows/windows.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download)
[![Avalonia 12.1](https://img.shields.io/badge/Avalonia-12.1-8B44AC)](https://avaloniaui.net)
[![SkiaSharp 3.119](https://img.shields.io/badge/SkiaSharp-3.119-0D9488)](https://github.com/mono/SkiaSharp)
[![Platform: Windows](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4?logo=windows&logoColor=white)](#building)
[![Architectures](https://img.shields.io/badge/architectures-x64%20%7C%20arm64-0078D4)](#building)
[![Tests](https://img.shields.io/badge/tests-161%20passing-brightgreen)](#testing)
[![Status: beta candidate](https://img.shields.io/badge/status-beta%20candidate-yellow)](#status)
[![Download beta](https://img.shields.io/badge/download-0.9.0--beta.1-blue?logo=github)](https://github.com/Revan67/Compositor-Windows/releases/tag/v0.9.0-beta.1)
[![Last commit](https://img.shields.io/github/last-commit/Revan67/Compositor-Windows)](https://github.com/Revan67/Compositor-Windows/commits/main)
[![Open issues](https://img.shields.io/github/issues/Revan67/Compositor-Windows)](https://github.com/Revan67/Compositor-Windows/issues)
[![Derived from Compositor](https://img.shields.io/badge/derived%20from-robbietilton%2FCompositor-lightgrey)](https://github.com/robbietilton/Compositor)

A free, open-source layered image editor for Windows, built around a Photoshop-style compositing workflow. The current beta candidate supports layered documents, folders, masks, clipping masks, blend modes, transforms, crop, rectangular selections, brush/eraser work, project persistence, crash recovery, resizing, and common image import/export. Retouching tools, adjustments, filters, editable text/shapes, and PSD/PSB interoperability remain on the roadmap.

This is an independent Windows application derived from [Compositor](https://github.com/robbietilton/Compositor) by Robbie Tilton, a macOS app written in Swift. The Windows version is a C# rewrite on [Avalonia](https://avaloniaui.net) and [SkiaSharp](https://github.com/mono/SkiaSharp); it uses the original behavior as a specification and reuses its C pixel kernels unchanged, but has its own project format and does not track the Mac app as a merge upstream.

## Download the public beta

[Download Compositor for Windows 0.9.0 Beta 1](https://github.com/Revan67/Compositor-Windows/releases/tag/v0.9.0-beta.1). The release includes the portable x64 ZIP, SHA-256 checksum, current feature list and testing instructions.

This is an unsigned prerelease and the UI is not final. Extract the full ZIP before running `Compositor.App.exe`, keep important source files backed up, and review the [beta testing guide](docs/beta-testing.md) before testing.

## Status

This is an unsigned beta candidate, not a production release. It is suitable for broader workflow testing, but not yet for irreplaceable production documents. See the [live port status](docs/port-status.md), [beta testing guide](docs/beta-testing.md), and [Windows port plan](docs/windows-port-plan.md).

| Phase | Scope | State |
| --- | --- | --- |
| 0 | Solution, native kernels, Avalonia window drawing through Skia | Done |
| 1 | Document model, sparse raster tiles, compositor (masks, folders, clipping, blend modes), image codec, project file, undo history, editing session | Done — headless |
| 2 | Shell UI: canvas, layers panel, menus, transform and crop tools, sheets | Core workflow working; polish remains |
| 3 | Selections, brush and retouching tools, adjustments, filters, live shapes and editable text | In progress: marquee, brush and eraser working |
| 4 | Layer effects, native PSD/PSB import and export, Remove Background, platform polish and installer | Planned |

The editor can create, open, save, import and export documents; navigate the canvas; manage layers; transform or crop content; create rectangular selections; and paint or erase on transformed layers with live preview, color/size/hardness/opacity controls, selection clipping and single-step undo. Diagnostics record reconstructable workflows for beta reports.

## Building

Requirements:

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Visual Studio 2022 with the **C++ Clang tools for Windows** component — used only to compile the C pixel kernels (`scripts/build-kernels.ps1` finds it through `vswhere`; no CMake)

```powershell
dotnet restore Compositor.slnx
dotnet build Compositor.slnx
dotnet test Compositor.slnx
& .\src\Compositor.App\bin\Debug\net10.0\Compositor.App.exe
```

To restore, build and test a clean x64 checkout in one command:

```powershell
.\scripts\verify.ps1
```

Add `-Publish` to also create a self-contained folder and ZIP under `dist/`. To publish a specific architecture directly, run `scripts\publish-windows.ps1 -Arch x64` or `-Arch arm64`. The publish smoke test verifies the executable, native DLL and required kernel exports; an arm64 native DLL must be load-tested on an arm64 host.

The GitHub Actions workflow builds and publishes CI artifacts for x64 and arm64. CI artifacts are development outputs, not signed releases.

## Testing

```powershell
dotnet test Compositor.slnx --configuration Release
```

The suite covers the native kernels, geometry, rendering, document/history behavior, project validation and round trips, canvas interactions, and diagnostics. For hands-on coverage, follow [docs/beta-testing.md](docs/beta-testing.md). Keep original assets backed up and report the smallest reproduction plus the newest diagnostic log.

Run `./scripts/stress.ps1 -Profile quick` for the CI-sized 2K paint/save/reopen workload, or `-Profile full` for the 4K/800 px beta workload. Both verify exact rendered pixels after save/reopen and emit timing/memory JSON. See [docs/stress-baseline.md](docs/stress-baseline.md) for the current baseline.

### Beta diagnostics

Every run writes a timestamped diagnostic log under `%LOCALAPPDATA%\Compositor\Logs`; **Help → Open Diagnostic Logs** opens that folder. It records build/runtime information, commands, tool and edit workflows, project I/O, Avalonia trace output and unhandled exceptions. The newest 20 logs are retained. When reporting an alpha failure, reproduce it once and attach the newest `compositor-*.log` file.

Modified documents are also autosaved every 45 seconds while idle to `%LOCALAPPDATA%\Compositor\Recovery`. After an interrupted session, startup offers to recover the snapshot as an unsaved project or discard it. Recovery never overwrites the original project and is cleared after an intentional save, open, new document, discard, or clean close.

## Layout

```
src/Compositor.Kernels   The original C pixel kernels, compiled to compositor_kernels.dll, plus P/Invoke bindings
src/Compositor.Core      Document model, raster engine, compositor, codec, project file, history. No UI dependency.
src/Compositor.App       Avalonia desktop app
tests/                   xUnit tests; one class per reference test suite where one exists
docs/                    Port plan and format notes
Compositor/, CompositorTests/, Compositor.xcodeproj   The original Mac source, kept as the reference design while the port is in progress
```

## Project files

`.comp` is a zip archive containing `manifest.json` and one PNG per layer (plus `.mask.png` for masks). The schema follows the original's layout but carries its own identifier (`com.compositor.windows.project`); files from the Mac app are not read. Saves are atomic: a sibling temp file is written, validated and swapped in.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Upstream macOS Compositor is reference material only: this repository is an independent C#/Avalonia rewrite, so upstream commits are reviewed and reimplemented rather than merged.

## Documentation

- [Port status](docs/port-status.md) — live feature matrix and immediate priorities
- [Beta testing](docs/beta-testing.md) — workflows and useful bug reports
- [Windows port plan](docs/windows-port-plan.md) — architecture, phases, risks and decisions
- [Project format](docs/project-format.md) — `.comp` schema and validation
- [Brush performance](docs/brush-performance.md) — reference implementation behavior and performance targets
- [Stress baseline](docs/stress-baseline.md) — repeatable Windows beta workload and current measurements
- [Beta plan](docs/beta-plan.md) — scope, gates and remaining blockers

## License

MIT — see [LICENSE](LICENSE). The original Compositor is © Wonder Assembly LLC; this port keeps that notice and adds its own changes under the same license.
