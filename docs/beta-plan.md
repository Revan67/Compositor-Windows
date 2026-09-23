# Beta plan

The first Windows beta is a reliability milestone for the implemented editor workflow, not feature parity with the macOS reference. PSD/PSB, advanced retouching, adjustments, text, effects, installer/signing and automatic updates are not beta-one blockers.

## Exit gates

| Gate | Requirement | State |
| --- | --- | --- |
| Core workflow | Create/open/save; layers; masks/clipping; transform; crop; selection; paint/erase; resize; import/export | Working; broader testing required. Layer order uses Up/Down controls or the row context menu; direct drag-and-drop remains follow-up work. |
| Data safety | Validated atomic save, malformed-project rejection, unsaved-change prompts, crash recovery that never overwrites the source | Implemented; manual recovery UX test pending |
| Performance | Quick stress in CI; full 4K/800 px workload passes exact round-trip without runaway memory; broader hardware results reviewed | Local candidate gate passed; broader hardware results pending |
| Diagnostics | Build identity, commands, edits, tools, paint, project I/O, recovery and exceptions are reconstructable | Implemented |
| UI readiness | Essential actions reachable, consistent enabled states/shortcuts, no dead primary controls, usable error messages | Canvas/Image Size and essential Layers interaction implemented; final hands-on checklist pending |
| Distribution | Clean self-contained x64 ZIP, native smoke test, tester guide and checksum; explicitly unsigned beta | `0.9.0-beta.1` candidate generated and locally verified; not published |
| Quality | Zero known data-loss/crash defects; Release suite and beta checklist green | In progress |

## Work order

1. Validate Layers-panel drag/reorder, eye swipe and multi-selection across nested and clipped stacks; add clearer drop affordances where needed.
2. Complete the minimum selection workflow needed for real editing: composition, marching ants and outline movement are done; selected-pixel movement remains. Defer advanced shapes and processing if necessary.
3. Harden brush ergonomics without reopening the renderer architecture: spacing/presets and right-drag size/hardness. Pressure is included only if the Avalonia pointer path is reliable across common devices.
4. Run repeated recovery, malformed-file, long-history, large-layer-stack and 4K stress sessions; fix every crash, corruption path and severe regression.
5. Freeze strings/project schema, produce an unsigned beta candidate, and run the handoff checklist on a clean Windows machine before publishing anything.

## Explicitly deferred from beta one

- Native PSD/PSB import/export
- HEIC/TIFF import
- Retouching tools, gradients, advanced shapes and editable text
- Adjustments, filters and non-destructive layer effects
- Remove Background
- ARM64 public artifact, installer, code signing and updater

These remain roadmap commitments. Deferral keeps the beta small enough to make reliable and useful quickly.
