# Compositor — Windows port plan

_Drafted 2026-09-19 from Compositor 1.0.4 (`a19db90`). Living document; update as decisions land._

## Goal

An **independent Windows application**, not a cross-platform build. Compositor 1.0.4's Swift source and tests are the reference design: every feature in the README, ported to C#, with no obligation to read Mac project files or to track later Mac releases. Lightweight to ship: a single self-contained folder or installer, no MSIX, no Windows App SDK, fast startup.

Consequences of "independent":
- The Mac source stays in the repo as reference until the port is complete, then moves to `reference/` or is removed.
- One native project format, owned by this app (see below). No directory packages, no Mac interop tests.
- Upstream is a one-time source, not a branch to merge. Features from later Mac releases get ported by reading them, if wanted.
- No Sparkle/appcast. The updater checks **GitHub Releases** on this repo (public), which needs no server of our own.

## Decision: target stack

**C# / .NET 10 + Avalonia 12 + SkiaSharp**, with the existing C pixel kernels compiled unchanged into a native DLL.

Why not the alternatives:

| Option | Why not |
| --- | --- |
| Swift 6 on Windows | Would keep `Document/` and the tests as source, but there is no UI framework — the 3.4k-line UI layer would be hand-rolled on Win32. Niche toolchain, poor debugging. Not lightweight in effort. |
| WinUI 3 | Windows App SDK runtime + MSIX packaging. Heaviest option to ship. |
| WPF | In-box runtime (lightest binary), but the canvas has to go through `WriteableBitmap` or a software blit every frame. Dark tool-UI needs full restyling. |
| C++ / Qt | Fine technically; not the maintainer's stack. |

Why Avalonia specifically: Avalonia renders through Skia, and SkiaSharp is the natural replacement for `CGImage`/`CGContext`. The canvas can draw `SKBitmap` tiles directly into Avalonia's GPU-backed surface with no per-frame copy. That matters for a 4K canvas with ~3 ms brush updates. Dark, dense, Photoshop-style chrome is the default look rather than a restyling job. Cost: ~15 MB framework-dependent / ~70 MB self-contained.

## What is in the reference source (1.0.4)

| Area | Files | LOC | Portability |
| --- | --- | ---: | --- |
| `Compositor/Document` | 41 Swift | 8,071 | Logic ports 1:1. ~150 `CGImage` / 28 `CGContext` sites need Skia. Almost no AppKit. |
| `Compositor/Rendering` | 15 Swift + 8 C | 4,462 + 778 | Compositor, tiles, snapshots, overlays. C kernels are 100 % portable (`stdint`, `math`, `string` only). Metal brush has a software fallback. |
| `Compositor/UI` | 28 Swift | 3,425 | SwiftUI + AppKit. Full rewrite in Avalonia XAML/C#. |
| `Compositor/IO` | 8 Swift | 1,020 | Project store, import/export, drag-drop, app delegate. ImageIO → SkiaSharp codecs; `FileWrapper`/`NSFileCoordinator` → single-file zip + `File.Replace`. |
| `CompositorTests` | 47 Swift | 7,175 | 45 of 47 import AppKit/Metal. They become the **specification** for the C# tests, not a drop-in. |
| `docs/project-format.md` | — | — | Complete v1–6 spec of `.comp`. Most valuable artifact for the port. |

### Apple API surface, sized

Of ~1,700 `CG*` references, ~1,250 are value types (`CGFloat` 461, `CGPoint` 344, `CGRect` 316, `CGSize` 96, `CGAffineTransform` 35). Those become `double`, `SKPoint`/`SKRect`/`SKSize`/`SKMatrix` or small structs in a shared `Geometry` namespace — mechanical.

The real porting surface:

| Apple API | Refs | Windows replacement |
| --- | ---: | --- |
| `CGImage` / `CGContext` / `CGColorSpace` / `CGBitmapInfo` | ~350 | `SKBitmap` / `SKCanvas` / `SKColorSpace`. Working space stays premultiplied sRGB BGRA8888 (Skia's native), byte order differs from CG's RGBA — the C kernels are channel-order-agnostic except where they read specific channels (check `layer_extract_alpha`, wand, heal). |
| `CGPath` / `CGMutablePath` / `NSBezierPath` | ~46 | `SKPath` |
| `CIFilter` (8 filters) | 11 files | `CIColorMatrix`, `CIColorClamp`, `CIColorCube`, `CIBlendWithMask`, `CIColorDodge/BurnBlendMode` → `SKColorFilter.CreateColorMatrix`, table filters, existing `SeparableBlend` + C. `CIMotionBlur` → own separable directional blur. `CIPerspectiveTransform` → `SKMatrix` perspective draw (Skia supports 3×3). Small. |
| `ImageIO` (JPEG/PNG/HEIC/TIFF, DPI metadata) | IO | `SKCodec` for PNG/JPEG/WebP. **HEIC**: Skia has no HEIC decoder on Windows — use the Windows Imaging Component (WIC) via P/Invoke or `Windows.Graphics.Imaging` for HEIC/TIFF input (HEVC extension must be installed for HEIC; document that). DPI: write pHYs (PNG) / JFIF density (JPEG) ourselves. |
| `vImage` (Accelerate) | 22 | `System.Numerics.Vector<T>` or plain loops; used for invert and downsampling only. |
| `Metal` (brush coverage compute kernel) | 1 file | Phase 1: software fallback (already exists, tested, 2.5 % / 1.5 % spacing). Phase 4: optional D3D12 compute via ComputeSharp if profiling says it matters. |
| `Vision` `VNGenerateForegroundInstanceMaskRequest` (Remove Background) | 1 file | No OS equivalent. **ONNX Runtime + a segmentation model** (RMBG-1.4 ≈ 44 MB, or ISNet). Ship as optional download on first use to keep the installer light. See Risks. |
| `Sparkle` + `appcast.xml` | 2 files | Own updater against the GitHub Releases API (see Phase 4). |
| `NSPasteboard` | 13 | Avalonia `IClipboard` + Win32 `CF_DIB`/PNG for cross-app paste. Layer drag between projects → Avalonia `DragDrop` with an in-process payload. |
| `NSCursor` (5 cursors) / `NSSound.beep` / `NSTrackingArea` | ~120 | Avalonia `Cursor` + custom cursor bitmaps for brush ring, `System.Media.SystemSounds.Beep`, pointer-moved events. |
| `NSTableView` layer list (`NativeLayerList.swift`) | 1 file | Avalonia `TreeDataGrid` or a custom `ItemsControl` with drag reorder, inline rename, eye-swipe. |
| `NSFileCoordinator` + `FileWrapper` atomic package write | `ProjectStore` | Single-file zip written to a sibling temp file, then `File.Replace`. See Format below. |

## Proposed solution layout

```
Compositor.slnx
Directory.Build.props                 net10.0, nullable, warnings-as-errors
src/
  Compositor.Kernels/                 C: the 8 .c/.h files, unchanged. scripts/build-kernels.ps1 (VS clang) → compositor_kernels.dll
  Compositor.Core/                    Pure C# + SkiaSharp. No Avalonia. Everything from Document/ + Rendering/ + IO/ProjectStore
    Geometry/                         Point, Rect, Size, Transform (CG value-type shim)
    Raster/                           RasterSnapshot, tiles, DownsampleCache, LayerRenderer, TiledLayerRenderer, SeparableBlend
    Document/                         ImageLayer, CanvasDocument, EditorSession(+Brush,+Projects), DocumentHistory, Selection…
    Tools/                            BrushStroke, CloneStamp, SpotHeal, Smudge/Liquify, MagicWand, Gradient, Shape, Crop, Distort
    Adjustments/                      Levels, Curves, HueSaturation, Exposure, GradientMap, Grain, Invert, Filters
    Project/                          ProjectStore (.comp zip), ImageImporter/Exporter, CanvasResizer, ImageResizer
  Compositor.App/                     Avalonia 12 desktop app. Views, view-models, canvas control, panels, sheets, menus
    Canvas/                           EditorCanvas (custom Control, ICustomDrawOperation over SKCanvas), overlays, cursors
    Panels/                           Layers, Tool header, Brush/Crop/Lasso/Shape/Gradient controls, TransformInspector
    Sheets/                           NewCanvas, CanvasSize, ImageSize, Levels, Curves, HueSaturation, Filter, JPEGExport, ColorPicker
tests/
  Compositor.Core.Tests/              xUnit. One test class per reference Swift suite; same names, same scenarios
  Compositor.Kernels.Tests/           Per-entry-point tests with the numeric expectations from the Swift suites
  (fixtures are hand-written manifests inside ProjectStoreTests, next to their assertions)
```

Rule enforced by a test: `Compositor.Core` must not reference Avalonia. This is what keeps the model portable and the tests fast.

## The `.comp` format on Windows

The Mac app's `.comp` is a directory package — a Finder concept with no Windows analogue (no folder-by-extension association, no atomic directory replace). This app owns its own format instead:

1. **`.comp` is a single zip file** containing `manifest.json` and `images/<layer UUID>.png` (+ `.mask.png`). Stored, not deflated — PNGs don't shrink. Double-click, file association, drag-drop and email all just work. Save writes a sibling temp file, validates it, then `File.Replace` — genuinely atomic.
2. **Schema starts at the Mac v6 layout** (`docs/project-format.md`) because it is a good, fully specified design: layers bottom-to-top, groups via `parentID`/`isGroup`, opacity/blend mode, raster masks, clipping masks via `maskSourceID`, folder masks, resolution. Manifest identifier becomes `com.compositor.windows.project`, version restarts at `1`. No reader for Mac versions 1–6.
3. **Free to evolve.** Additive fields bump the version; the reader rejects unknown newer versions, same policy as the reference. Undo history and viewport stay session-only.
4. **Validation limits carried over** because they are sensible: 30k px/side, 100 M source + 100 M mask px, 10k layers, 4 MiB manifest, 512 MiB/asset, no `..`/absolute entry names, all checked before the live document is replaced.

## Phases

Each phase ends with something runnable and its tests green. Sizes are relative, not dates.

### Phase 0 — Scaffold (small)
- Solution, `Directory.Build.props`, CI-less local build script.
- `Compositor.Kernels`: clang build of the eight C files → DLL; P/Invoke bindings (in the Kernels project); smoke test per entry point.
- `Compositor.Core` with Geometry shim and SkiaSharp; `Compositor.App` showing an empty Avalonia window drawing an `SKBitmap`.
- Exit: `dotnet build` and `dotnet test` pass. Core creates an `SKBitmap`; App draws it through `ICustomDrawOperation` + `ISkiaSharpApiLeaseFeature` with **one** SkiaSharp version resolved across the solution (pin Core to exactly the version Avalonia pins, in `Directory.Build.props`). A mismatched SkiaSharp is the cheapest bug to find here and the most expensive to find in Phase 2.

### Phase 1 — Document model and project I/O (large)
Order matters: the tile/snapshot layer first, because every other piece (renderer, brush commit, undo, save) sits on it and it has the real design content; `ProjectStore` last, once the raster representation it serializes has settled.
- Geometry shim (`Point`, `Size`, `Rect`, `Transform`) and `RasterSnapshot` tiling (256×256, immutable shared tiles, lazy contiguous bitmap, spatial-index flattening per `docs/brush-performance.md`).
- Port `EditorSession`, `ImageLayer`, `CanvasDocument`, `LayerTransform`, `LayerGroups`, `LayerAppearance`, `LayerMask`, `LiveLayerMask`, `DocumentHistory` (undo/redo), `Selection`.
- Port `LayerRenderer` / `TiledLayerRenderer` / `SeparableBlend` (all nine blend modes, opacity, masks, folder masks, clipping masks, adjustment layers).
- Import PNG/JPEG (Skia) and HEIC/TIFF (WIC); export PNG/JPEG with resolution metadata.
- Port `ProjectStore` as the zip-based `.comp` reader/writer with all validation. Round-trip tests against a hand-authored fixture set: groups, masks, folder masks, clipping masks, adjustment layers, shapes, plus every rejection case. See Verification below.
- Exit: a headless test opens every fixture and renders it; every rejection case in `ProjectStore.swift` is a test; save → open round-trips identically; the ported `ProjectTests`, `GroupTests`, `LayerMaskTests`, `LiveMaskTests`, `LayerAppearanceTests`, `RasterSnapshotTests` and `HistoryTests` pass with the same assertions as the reference. (`TiledLayerRenderer` and its tests are live-stroke preview and zoomed-out drawing: Phase 2/3.)

## Verification

No Mac is available and none is needed; nothing in this plan depends on running the original app.

- **Fixtures** are written by hand in our own format. The reference spec names every field, default and rejection rule, which is enough to author valid and deliberately-invalid projects.
- **Expected pixel output** comes from the reference Swift test suite. The 47 suites assert exact pixel values, coverage, bounds and histogram numbers for blends, masks, brush strokes, adjustments and filters. Each suite is ported to a C# test class of the same name with the same numbers; they are the specification.
- **What this does not catch:** things the reference never asserted on (downsampling filter choice, exact antialiasing of marching ants). Those are cosmetic and reviewed by eye — and since this is an independent app, matching the Mac pixel-for-pixel is not a goal anyway.

### Phase 2 — Shell UI (large)
- Main window: project tabs, tool rail, tool header, canvas, layers panel, floating panels.
- `EditorCanvas`: custom Avalonia control with `ICustomDrawOperation`; zoom/pan/fit, pixel grid when zoomed in, high-quality downsample cache when zoomed out, checkerboard.
- Layers panel: `NativeLayerList` behaviours — nesting, drag reorder, Alt-drag duplicate, inline rename, eye swipe, dimmed hidden rows, clip indentation, drag between projects.
- Menus with Photoshop shortcuts (⌘ → Ctrl, ⌥ → Alt). Keep the `NavigationTool` single-key shortcuts.
- Sheets: New Canvas, Canvas Size, Image Size, JPEG Export (live preview), Color Picker.
- Move/Transform tool with snapping, guides, handles, free distort (Ctrl-drag), Transform Inspector.
- Crop with snapping and Alt-symmetric.
- Exit: open, view, arrange, transform, crop, save. Everything in the README's "Layers", "Transform", "Canvas and files" sections except painting.

### Phase 3 — Tools and adjustments (large)
- Selections: Marquee (rect/ellipse), Lasso (free/polygon), Magic Wand (C `wand_mask`/`wand_trace`), add/subtract, move outline, move/duplicate pixels, load layer/mask as selection, marching ants overlay, edge autoscroll, Shift constrain.
- Brush (software coverage path from `BrushStroke.swift`), Eraser mode, Shift straight lines, right-drag size/hardness, brush cursor overlay.
- Spot Healing (C `spot_heal`), Clone Stamp (aligned/unaligned, current/all layers, sample ring overlay), Smear (Liquify/Blur/Smudge), Burn/Dodge, Gradient, Shape (live shape layers), Eyedropper.
- Content-Aware Fill (C `content_fill`) including canvas extension.
- Adjustments and adjustment layers: Levels (+Auto, C), Curves, Hue/Saturation, Exposure, Gradient Map (C), Grain (C), Invert; live preview limited to selection.
- Filters: Gaussian Blur and Motion Blur spreading past layer edges, Add Noise (C), Lens Correction (C), Flip layer/canvas.
- Copy Merged, clipboard in/out, screenshot paste.
- Exit: every README feature except Remove Background works; C# tests cover each upstream test suite.

### Phase 4 — Platform polish and the two hard features (medium)
- **Remove Background**: ONNX Runtime (CPU, DirectML optional) + RMBG-1.4 or ISNet-general. Model downloaded on first use into `%LOCALAPPDATA%\Compositor\models`. Same UX as the reference (menu item, undoable).
- **GPU brush**: only if profiling the software path on a 4K canvas / 800 px tip shows it above the reference numbers in `docs/brush-performance.md` (2.6–3.1 ms median). ComputeSharp (D3D12) port of the Metal kernel; keep the software path as fallback.
- **Updater via GitHub Releases.** On launch (and from Help → Check for Updates) call `GET https://api.github.com/repos/Revan67/Compositor-Windows/releases/latest`, compare the tag to the running version, and offer to download the release asset (installer or zip) and run/replace. Unauthenticated API is rate-limited to 60 req/h per IP, which is plenty for once-per-launch. Verify the asset with a SHA-256 listed in the release notes before running it. No appcast, no server.
- Release pipeline: `dotnet publish -r win-x64 --self-contained` (arm64 too), Inno Setup installer + plain zip, uploaded as release assets by a GitHub Actions workflow on tag push.
- File associations (`.comp`, images), "Open with", drag onto exe.
- High-DPI, multi-monitor, per-monitor scaling; Windows 11 dark title bar.
- Explorer thumbnail for `.comp` (optional, shell extension — probably skip).

## Risks and open questions

| Risk | Impact | Mitigation |
| --- | --- | --- |
| **Remove Background** needs a model | Only feature with no OS API on Windows | ONNX Runtime + downloadable model. Quality differs from Apple's Vision model — set expectations. Cut candidate only if the model download is unacceptable. |
| **HEIC import** | Skia has no HEIC on Windows | WIC path; requires Microsoft HEVC extension (free with most OEM Windows, else $0.99). TIFF also via WIC. |
| **Channel order** (CG RGBA vs Skia BGRA) | Silent colour swaps in C kernels that read named channels | Choose one working order for the whole Core (BGRA premul, Skia-native) and audit each C entry point once; golden tests catch the rest. |
| **Brush latency on CPU** | Feels worse than the reference on large soft tips | Tile-parallel software path (`Parallel.For` over dirty tiles), then Phase 4 GPU if needed. Measure with the same 4K/800 px benchmark. |
| **No reference app to run** | Can't compare against real output | Fixtures from the spec; Swift test assertions as the oracle (see Verification). |
| **Test-suite translation** | 7k lines of tests are the real spec; skipping them reintroduces the recurring "vacuous test" problem | One C# test class per reference suite, same names. Track coverage as a checklist in `docs/port-status.md`. |
| **Updater trust** | Running a downloaded exe | HTTPS to api.github.com only, SHA-256 from release notes checked before launch, user confirms. Optional: Authenticode-sign the installer. |
| **Fonts / SF Symbols** | Tool icons are SF Symbols names | Fluent System Icons or Lucide for the 15 tool glyphs. |

## Not in scope

- macOS-only chrome: unified toolbar, `handlesExternalEvents`, window fade, Dock behaviour.
- Notarization / DMG scripts (`scripts/release.sh`, `scripts/dmg/`) — replaced by a Windows packaging script.
- UI tests (`CompositorUITests`, 2 files) — replace with Avalonia headless tests later if wanted.

## License

MIT fork. A rewrite in another language is still derivative: keep `LICENSE`, carry the upstream copyright and a link to the original project in the Windows app's About box.

## First concrete tasks

1. Phase 0 scaffold: solution, Kernels DLL, P/Invoke, Avalonia window drawing a Core-created `SKBitmap` through the Skia lease.
2. Hand-author the fixture set in `tests/fixtures/` from the format spec (one per format version and feature, plus the invalid cases).
3. Port `ProjectStore` read path + validation for both containers, with the fixtures as tests. This is the earliest point where "opens the same files" can be proven.

## Status

### Phase 0 — done 2026-09-19
- `Compositor.slnx`, `Directory.Build.props` (net10.0, warnings as errors, Avalonia 12.1.2, SkiaSharp pinned to Avalonia's transitive 3.119.4).
- `Compositor.Kernels`: the eight reference C files unchanged + `KernelsExports.c` (`kernels_free`, so `wand_trace` buffers are released by the DLL's own CRT). Built by `scripts/build-kernels.ps1` with VS's clang via `vswhere` (no CMake, no vcxproj); the csproj runs it before build and copies the DLL to every consumer. `-D_USE_MATH_DEFINES` is the only MSVC-CRT accommodation. C `long` is 32-bit on Windows → `int` in P/Invoke.
- `Compositor.Core`: `Raster/Checkerboard` (BGRA8888 premul working format). No Avalonia reference; a test enforces it.
- `Compositor.App`: Avalonia 12, Fluent dark, `SkiaBitmapView` drawing an `SKBitmap` through `ISkiaSharpApiLeaseFeature` with no copy. Verified on screen.
- Tests: xunit.v3 on Microsoft.Testing.Platform (`global.json` opts in; .NET 10 SDK no longer runs MTP through VSTest). `dotnet test` at the root runs both projects: 9/9 pass, each asserting real kernel output.
- Run: `dotnet build` then `src\Compositor.App\bin\Debug\net10.0\Compositor.App.exe`; `dotnet test` for tests.

Next: Phase 2 — shell UI. Start with the editor canvas (zoom/pan/fit, checkerboard, `DocumentRenderer` through the Skia lease) and the layers panel bound to `EditorSession`.

### Phase 1 — done 2026-09-19
110 tests in `Compositor.Core.Tests`, 114 across the solution, all green:
- `Geometry/`: `Point`, `Size`, `Rect` (CG null-rect semantics), `AffineTransform` (CG layout and chaining order) with `ToSK()`.
- `Raster/`: `Bitmaps` (BGRA8888 premul images, Gray8 masks, copy-not-blend, zero-copy `AsImage`), `ImportedImage` (contiguous or snapshot-backed, lazy), `RasterSnapshot` with the reference `Replacing` handoff (256-cell spatial index, patch splitting, crop, alignment) and lazy materialization.
- `Document/`: `LayerTransform` (+ `TransformDrag`, `TransformSnap`), `ImageLayer`, `CanvasDocument`, `LayerMask` (+ placement, `ClipImage`, `Background`), `DocumentSelection`, `LayerBlendMode`, `LayerHierarchy` (entries, visibility inheritance, validation of tree and clipping links).
- `Rendering/`: `LayerRenderer` (SaveLayer + DstIn mask multiplication; opacity/blend on the saved layer so folder masks never isolate blending), `DocumentRenderer` (folder masks, clipping stacks via the C alpha kernels, non-adjacent clipping via rendered coverage). Skia's Color Dodge/Burn match the PDF formulas with translucent sources — verified by test — so `SeparableBlend` is not ported. `DownsampleCache` is replaced by mipmapped linear sampling when shrinking.
- `Project/`: `ImageCodec` (PNG/JPEG/WebP decode with EXIF orientation, PNG `pHYs` and JFIF density written by hand), `ProjectStore` (zip `.comp`, `com.compositor.windows.project` v1, validated temporary archive followed by an atomic same-volume replacement), `ProjectSnapshot` ↔ `CanvasDocument`.
- `Kernels/PixelKernels`: typed wrappers over the C entry points the renderer needs.
- `Document/DocumentHistory`: snapshot undo/redo with nested edits, no-op suppression, saved revision, entry and retained-byte limits (counting snapshot tiles as well as contiguous bitmaps).
- `Document/EditorSession`: the headless document-mutation core — create/open, selection and mask target, blank and pixel layers, delete (single and multi, releasing clipping links), rename, visibility and eye swipe, reorder (list order) and move by offset, duplicate and drop-duplicate, opacity drag and blend mode, folders (add, group selection, place, move out, collapse), masks (add, toggle, delete, replace, link), clipping masks (link, release, Alt-click toggle, adopt-on-drop, release-when-detached), transforms with linked/unlinked mask placement. `IsBusy` is the one hook the UI uses to refuse edits during modal operations; tool and view state stay out of Core.
- Reference suites ported with their assertions: `RasterSnapshotTests` (model half), `TransformTests` (geometry half), `HistoryTests`, `LayerTests`, `GroupTests`, `LayerMaskTests`, `LiveMaskTests`, `LayerAppearanceTests`, `ProjectTests`, `ExportTests`, `JPEGExportTests`, `ImageImportTests` (codec half). The tool-driven halves port with their tools in Phase 3.

Carried forward:
- `TiledLayerRenderer` (419 lines): the live brush-stroke preview (`drawStroke`, `drawMaskStroke`, `drawReplacing`) and the halving-grid alignment for zoomed-out drawing. Lands with the canvas in Phase 2 and the brush in Phase 3, with `TiledLayerTests`.
- HEIC/TIFF import through WIC (Skia has neither on Windows) — Phase 2, with the import UI.
- Adjustment-layer and shape-layer fields on `ImageLayer`/manifest arrive with Phase 3 (manifest version bump).
- `SKImage.FromPixels` wraps bitmap memory without owning it; fine for raster draws, to be revisited when tiles go through the GPU canvas in Phase 2.

### Phase 2 — in progress (2026-09-19)
First increment, runnable:
- `Rendering/CanvasViewport` in Core (fit, anchored zoom, pan, resize keeping the centre point), with the reference viewport tests.
- `Compositor.App`: `EditorViewModel` (session + viewport + cached composite + layer rows + commands), `EditorCanvas` (Skia lease; checkerboard, composite with mipmapped shrink / nearest enlarge, pixel grid from 8×, wheel pan, Ctrl+wheel zoom about the pointer, middle/Space drag pan, file drop imports at the drop point), `LayersPanel` (rows top-first with thumbnails, folder indent, clip arrow, mask badge, eye toggle, active highlight; blend mode and opacity for the active layer with a one-undo drag; context menu; bottom actions), `NewCanvasWindow`, rename prompt, discard-changes confirm, menus with Photoshop-style shortcuts (Ctrl+N/O/S/Shift+S, Z/Shift+Z/Y, Shift+N, J, G, [, ], +, -, 0, 1), status bar with zoom readout, command-line files (`.comp` opens; images make a canvas and import).
- Verified on screen: two images imported, composited at fit zoom, rows and controls populated.

Next in Phase 2: Move/Transform tool (handles, rotate, snapping, Transform Inspector), multi-select and drag-reorder in the layers panel, Crop, Canvas Size / Image Size sheets, JPEG export sheet with preview, project tabs, HEIC/TIFF via WIC. Rendering note: the composite is rebuilt whole on every document change (fine at 1080p, will need tile/region invalidation before the brush lands).
