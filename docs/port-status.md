# Windows port status

_Updated 2026-09-20. This is the concise live status; `windows-port-plan.md` retains the architectural rationale and longer phase history._

## Working in the broader alpha

| Area | Current coverage |
| --- | --- |
| Documents | New canvas; validated atomic `.comp` save/open; dirty state; discard confirmation; undo/redo |
| Canvas | Fit, zoom, pan, checkerboard, pixel grid, drag-and-drop import |
| Layers | Add, duplicate, delete, reorder, group, visibility, opacity, blend modes, masks and clipping masks |
| Transform | Move, resize, rotate, flip, sampling, inspector, snapping, keyboard nudging |
| Crop and selection | Crop workflow; rectangular marquee; select all; deselect; selection-clipped painting |
| Painting | Round brush and eraser; live preview; size and opacity; transformed-layer mapping; one undo entry per stroke |
| Files | PNG/JPEG/WebP import; PNG/JPEG export; command-line/open-with input |
| Diagnostics | Per-run build/runtime log; named commands; edit/history, tool, selection and paint events; project I/O; exceptions; newest 20 logs retained |
| Delivery | Reproducible self-contained x64/arm64 ZIP pipeline; PE/native export smoke checks; unsigned CI artifacts |

## Partial or intentionally limited

| Area | Limitation |
| --- | --- |
| Brush | Black foreground only; no color picker, hardness, spacing, pressure, straight-line gesture, brush cursor, or sparse tiled commit yet |
| Selection | Rectangular marquee only; no add/subtract, ellipse/lasso/wand, feather, marching ants, or pixel move |
| Layer panel | Core operations work; advanced multi-select and drag/reorder polish remain |
| Image formats | HEIC/TIFF via WIC is not implemented |
| Packaging | Portable unsigned ZIP only; no installer, signing, updater, or public release |

## Planned

- Remaining selection and paint controls, then retouching tools, gradients and live shapes.
- Adjustments, adjustment layers, filters and editable text.
- Non-destructive layer effects.
- Native layered PSD/PSB import and export with explicit fallback/preflight behavior.
- Remove Background, Windows polish, installer/signing and release/update flow.

## Immediate priorities

1. Use the instrumented broader alpha to collect real workflow failures.
2. Add color controls, hardness/spacing, straight-line brush gestures and a brush cursor.
3. Expand selection workflows and layer-panel interaction.
4. Profile large-document painting before choosing tiled/GPU optimization work.

## Quality gate

The current solution has 152 passing automated tests. A handoff build must also complete a clean Release build, publish successfully, pass x64 native DLL loading/export checks, and include `ALPHA-TESTING.md`. No public release is authorized yet.
