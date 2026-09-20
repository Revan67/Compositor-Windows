# Compositor for Windows

[![License: MIT](https://img.shields.io/github/license/Revan67/Compositor-Windows)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download)
[![Avalonia 12](https://img.shields.io/badge/Avalonia-12-8B44AC)](https://avaloniaui.net)
[![SkiaSharp](https://img.shields.io/badge/SkiaSharp-3.119-0D9488)](https://github.com/mono/SkiaSharp)
[![Platform: Windows](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4?logo=windows&logoColor=white)](#building)
[![Status: work in progress](https://img.shields.io/badge/status-work%20in%20progress-orange)](#status)
[![Last commit](https://img.shields.io/github/last-commit/Revan67/Compositor-Windows)](https://github.com/Revan67/Compositor-Windows/commits/main)
[![Derived from Compositor](https://img.shields.io/badge/derived%20from-robbietilton%2FCompositor-lightgrey)](https://github.com/robbietilton/Compositor)

A free, open-source layered image editor for Windows, built around a Photoshop-style compositing workflow: layers and folders, masks, clipping masks, blend modes, non-destructive transforms, selections, brushes, retouching tools, adjustments and filters.

This is an independent Windows application derived from [Compositor](https://github.com/robbietilton/Compositor) by Robbie Tilton, a macOS app written in Swift. The Windows version is a C# rewrite on [Avalonia](https://avaloniaui.net) and [SkiaSharp](https://github.com/mono/SkiaSharp); it keeps the original's features and reuses its C pixel kernels unchanged, but has its own project format and does not track the Mac app.

## Status

Work in progress. The port is being built in phases; see [docs/windows-port-plan.md](docs/windows-port-plan.md) for the plan, decisions and what is done.

| Phase | Scope | State |
| --- | --- | --- |
| 0 | Solution, native kernels, Avalonia window drawing through Skia | Done |
| 1 | Document model, sparse raster tiles, compositor (masks, folders, clipping, blend modes), image codec, project file, undo history, editing session | Done — headless |
| 2 | Shell UI: canvas, layers panel, menus, transform and crop tools, sheets | In progress |
| 3 | Selections, brush and retouching tools, adjustments, filters | |
| 4 | Remove Background (ONNX), GPU brush if needed, updater via GitHub Releases, installer | |

The Phase 2 shell can create, open, save, import and export documents; navigate the canvas; manage layers; and transform or crop content. Painting and selection tools are not implemented yet. The current solution has 144 automated tests.

## Building

Requirements:

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Visual Studio 2022 with the **C++ Clang tools for Windows** component — used only to compile the C pixel kernels (`scripts/build-kernels.ps1` finds it through `vswhere`; no CMake)

```
dotnet build
dotnet test
src\Compositor.App\bin\Debug\net10.0\Compositor.App.exe
```

To restore, build and test a clean x64 checkout in one command:

```powershell
.\scripts\verify.ps1
```

Add `-Publish` to also create a self-contained folder and ZIP under `dist/`. To publish a specific architecture directly, run `scripts\publish-windows.ps1 -Arch x64` or `-Arch arm64`. The publish smoke test verifies the executable, native DLL and required kernel exports; an arm64 native DLL must be load-tested on an arm64 host.

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

## License

MIT — see [LICENSE](LICENSE). The original Compositor is © Wonder Assembly LLC; this port keeps that notice and adds its own changes under the same license.
