# Compositor for Windows 0.9.0-beta.1 — Testing Guide

This is an unsigned, portable beta candidate for Windows 10 and 11 x64. It is intended for broader workflow testing, not irreplaceable production work. Extract the entire ZIP before running `Compositor.App.exe`; Windows may show a SmartScreen warning because the build is not code-signed.

## Before testing

- Keep original images and important `.comp` projects backed up.
- PSD/PSB import and export are planned but are not implemented in this build.
- Reorder layers with the Up/Down buttons or **Move Layer Up/Down** in the layer context menu. Direct row drag-and-drop is not supported in this candidate.

## Core checklist

1. Create a canvas, import PNG/JPEG/WebP content, add layers, change visibility and order, then save and reopen the `.comp` project.
2. Move, resize, rotate and crop imported content; verify undo and redo at each stage.
3. Create, add to, subtract from and intersect rectangular selections. Paint across selection edges, erase, save and reopen.
4. Exercise folders, masks, clipping masks, opacity, blend modes, PNG export and JPEG export.
5. Use **Image → Canvas Size** with several anchors and optional fill, then **Image → Image Size** with aspect lock and different sampling modes. Undo and redo both operations.
6. Ctrl-click multiple layers, then group or delete them and confirm the operation is a single undo step.

## Recovery check

1. Import an image or paint a clearly visible mark.
2. Wait at least six seconds after the edit.
3. End Compositor from Task Manager without closing it normally.
4. Restart and choose **Recover**.
5. Confirm the recovered project contains the actual layer pixels and opens as unsaved. Saving it must not overwrite the prior source project unless you explicitly choose that path.

## Reporting a problem

Include the expected result, actual result, shortest reproduction, relevant `.comp` or source image when safe, and the newest diagnostic log. Use **Help → Open Diagnostic Logs** or open `%LOCALAPPDATA%\Compositor\Logs`.

Logs include commands, edits, local file paths, runtime details and exceptions, but not image pixels. Recovery data under `%LOCALAPPDATA%\Compositor\Recovery` can contain document pixels; review it before sharing.
