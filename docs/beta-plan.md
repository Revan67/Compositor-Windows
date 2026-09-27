# Beta plan

The first Windows beta was a reliability milestone for the implemented editor workflow, not feature parity with the macOS reference. `v0.9.0-beta.1` was published on 2026-09-22. This document now tracks stabilization of the existing UI, export-control and portable-updater work for the Beta 2 development checkpoint; it does not expand the editor roadmap.

## Exit gates

| Gate | Requirement | State |
| --- | --- | --- |
| Core workflow | Create/open/save; layers; masks/clipping; transform; crop; selection; paint/erase; resize; import/export | Packaged candidate checklist passed. Layer order uses Up/Down controls or the row context menu; direct drag-and-drop remains follow-up work. |
| Data safety | Validated atomic save, malformed-project rejection, unsaved-change prompts, crash recovery that never overwrites the source | Automated coverage and real-pixel recovery check passed |
| Performance | Quick stress in CI; full 4K/800 px workload passes exact round-trip without runaway memory; broader hardware results reviewed | Local candidate gate passed; broader hardware results pending |
| Diagnostics | Build identity, commands, edits, tools, paint, project I/O, recovery and exceptions are reconstructable | Implemented |
| UI readiness | Essential actions reachable, consistent enabled states/shortcuts, no dead primary controls, usable error messages | Packaged candidate checklist passed; documented drag-reorder limitation remains |
| Distribution | Clean self-contained x64 and ARM64 ZIPs, architecture checks, tester guide and checksums; explicitly unsigned beta | Beta 2 candidate packages pass local structure and architecture checks; publication and ARM64 hardware validation remain |
| Quality | Zero known data-loss/crash defects; Release suite and beta checklist green | Beta 1 gate passed on 2026-09-22; Beta 2 checkpoint verification is tracked below |

## Beta 2 checkpoint work order

1. Stabilize and commit the existing UI-polish, zoom, export-control, crop-preset, updater, packaging and documentation batch.
2. Keep the complete Release suite green and verify the publish output continues to match the updater's architecture-specific asset naming convention.
3. Exercise zoom controls and PNG/JPEG export options in the packaged build; exercise update discovery and checksum verification against a controlled prerelease before publishing Beta 2.
4. Run the Beta 2 handoff checklist on a clean Windows machine. Do not treat the local checkpoint alone as authorization to publish another release.

## Explicitly deferred from beta one

- Native PSD/PSB import/export
- HEIC/TIFF import
- Retouching tools, gradients, advanced shapes and editable text
- Adjustments, filters and non-destructive layer effects
- Remove Background
- Installer and code signing
- Automatic in-place replacement of a running portable installation; the implemented updater only discovers, downloads, verifies and stages ZIP releases for manual replacement

These remain roadmap commitments. Deferral keeps the beta small enough to make reliable and useful quickly.
