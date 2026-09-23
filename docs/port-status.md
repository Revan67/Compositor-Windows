# Windows port status

_Updated 2026-09-20. This is the concise live status; `windows-port-plan.md` retains the architectural rationale and longer phase history._

## Working in the broader alpha

| Area | Current coverage |
| --- | --- |
| Documents | New canvas; validated atomic `.comp` save/open; dirty state; discard confirmation; undo/redo; 45-second crash-recovery autosave |
| Canvas | Fit, zoom, pan, checkerboard, pixel grid, drag-and-drop import |
| Layers | Add, multi-select, duplicate, delete, command/direct-drag reorder, group, visibility/eye swipe, opacity, blend modes, masks and clipping masks |
| Transform | Move, resize, rotate, flip, sampling, inspector, snapping, keyboard nudging |
| Crop and selection | Crop workflow; rectangular marquee; replace/add/subtract/intersect; animated marching ants; drag/keyboard outline movement; select all; deselect; selection-clipped painting |
| Painting | Round brush and eraser; live preview; color, size, hardness and opacity; Shift-click lines; size-aware cursor; transformed-layer mapping; one undo entry per stroke |
| Files | PNG/JPEG/WebP import; PNG/JPEG export; Canvas Size and Image Size; command-line/open-with input |
| Diagnostics | Per-run build/runtime log; named commands; edit/history, tool, selection and paint events; project I/O; exceptions; newest 20 logs retained |
| Delivery | Reproducible self-contained x64/arm64 ZIP pipeline; PE/native export smoke checks; unsigned CI artifacts; repeatable 2K/4K stress runner |

## Partial or intentionally limited

| Area | Limitation |
| --- | --- |
| Brush | Hex color entry rather than a full picker; no spacing, pressure, right-drag adjustment, presets, or sparse tiled commit yet |
| Selection | Rectangular marquee composition and outline movement work; no ellipse/lasso/wand, feather, edge editing, or selected-pixel move yet |
| Layer panel | Core multi-select, drag/reorder and eye-swipe workflows work; deeper nesting/drop affordances and visual polish remain |
| Image formats | HEIC/TIFF via WIC is not implemented |
| Packaging | Portable unsigned ZIP only; no installer, signing, updater, or public release |

## Planned

- Remaining selection and paint controls, then retouching tools, gradients and live shapes.
- Adjustments, adjustment layers, filters and editable text.
- Non-destructive layer effects.
- Native layered PSD/PSB import and export with explicit fallback/preflight behavior.
- Remove Background, Windows polish, installer/signing and release/update flow.

## Immediate priorities

1. Use the instrumented broader alpha to collect real workflow failures and validate recovery UX.
2. Validate layer drag/reorder, multi-select and eye-swipe behavior across nested/clipped stacks; refine drop affordances.
3. Add selection outline/pixel movement, then expand selection shapes and brush ergonomics.
4. Keep the 4K stress baseline green; optimize preview invalidation if broader hardware falls below the beta bar.

## Quality gate

The current solution has 161 automated tests. A handoff build must also complete a clean Release build, pass the quick stress workload, publish successfully, pass x64 native DLL loading/export checks, and include `ALPHA-TESTING.md`. No public release is authorized yet.
