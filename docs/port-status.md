# Windows port status

_Updated 2026-09-27. This is the concise live status; `windows-port-plan.md` retains the architectural rationale and longer phase history._

## Working in the beta candidate

| Area | Current coverage |
| --- | --- |
| Documents | New canvas; validated atomic `.comp` save/open; dirty state; discard confirmation; undo/redo; edit-debounced and periodic crash recovery |
| Canvas | Fit, explicit zoom in/out modes, status-bar zoom controls, 100%, pan, checkerboard, pixel grid, drag-and-drop import |
| Layers | Add, multi-select, duplicate, delete, Up/Down and context-menu reorder, group, visibility, opacity, blend modes, masks and clipping masks |
| Transform | Move, resize, rotate, flip, sampling, inspector, snapping, keyboard nudging |
| Crop and selection | Crop workflow with landscape and portrait ratio presets; rectangular marquee; replace/add/subtract/intersect; animated marching ants; drag/keyboard outline movement; select all; deselect; selection-clipped painting |
| Painting | Round brush and eraser; live preview; color, size, hardness and opacity; Shift-click lines; size-aware cursor; transformed-layer mapping; one undo entry per stroke |
| Files | PNG/JPEG/WebP import; PNG lossless compression and JPEG quality controls; Canvas Size and Image Size; command-line/open-with input |
| Diagnostics | Per-run build/runtime log; named commands; edit/history, tool, selection and paint events; project I/O; exceptions; newest 20 logs retained |
| Delivery | Reproducible self-contained x64/arm64 ZIP pipeline; PE/native export smoke checks; unsigned CI artifacts; repeatable 2K/4K stress runner; GitHub Releases update checks and checksum-verified portable download staging |

## Partial or intentionally limited

| Area | Limitation |
| --- | --- |
| Brush | Hex color entry rather than a full picker; no spacing, pressure, right-drag adjustment, presets, or sparse tiled commit yet |
| Selection | Rectangular marquee composition and outline movement work; no ellipse/lasso/wand, feather, edge editing, or selected-pixel move yet |
| Layer panel | Multi-select and explicit reorder/visibility controls work; direct drag reorder, eye swipe, deeper nesting affordances and visual polish remain |
| Image formats | HEIC/TIFF via WIC is not implemented |
| Packaging | Portable unsigned ZIP and public beta; updater stages and verifies downloads but cannot replace the running portable folder; no installer or signing yet |

## Planned

- Remaining selection and paint controls, then retouching tools, gradients and live shapes.
- Adjustments, adjustment layers, filters and editable text.
- Non-destructive layer effects.
- Native layered PSD/PSB import and export with explicit fallback/preflight behavior.
- Remove Background, remaining Windows polish, installer/signing and automatic replacement flow.

## Upstream 1.2.9 review

Upstream changes through `01e8e52` were reviewed on 2026-09-23 as behavioral and product reference only. We do not merge upstream code.

### Adopt now

- Add the upstream 3:4 and 9:16 crop presets.
- Carry upstream's new Color Dodge/Burn sRGB and semi-transparent Levels cases into our regression plan before those paths expand.

### Next core-workflow milestones

1. Whole-layer/folder and multi-selected-layer duplicate/copy/paste, including between open projects.
2. Image Trim, crop-from-selection, persistent tool defaults and optional auto-select-under-pointer.
3. Native PSD/PSB import, beginning with 8-bit RGB raster layers, folders, masks and supported blend modes; always report flattened or omitted constructs. Layered PSD/PSB export remains a separate follow-up milestone.
4. Rulers, guides, grid and snapping, followed by folder opacity and the remaining Photoshop blend modes. Folder opacity requires an independent Windows project-schema revision.

### Later advanced work

- Editable PSD text metadata, non-destructive adjustment layers and effects (including inner/outer glow), finishing filters, brush smoothing, object selection, RAW development and shortcut remapping.
- Revisit fixed pixel budgets only with representative large-document stress data; upstream's memory-scaled strategy is useful reference but is not automatically safer for the beta.

### Not applicable to the Windows rewrite

- Sparkle/appcast updates, macOS title-bar and tab behavior, notarization and other platform-only changes.

## Immediate priorities

1. Commit and pass CI with the coherent Beta 2 checkpoint batch already in the working tree.
2. Exercise the packaged zoom/export workflows and updater against a controlled newer prerelease.
3. Run the Beta 2 handoff checklist on a clean Windows machine before publishing.
4. Keep roadmap features frozen until this checkpoint is reproducible.

## Quality gate

The public prerelease `v0.9.0-beta.1` passed 161 automated tests, the full 4K stress workload, packaged x64 native DLL loading/export checks, real-pixel crash recovery, and the hands-on beta checklist on 2026-09-22. Its portable ZIP includes `BETA-TESTING.md` and has a SHA-256 checksum. The unreleased Beta 2 checkpoint suite contains 177 tests after updater, zoom negative-path, and crop-preset resize coverage; publishing still requires CI and final release approval.
