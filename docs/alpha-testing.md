# Compositor Windows — Alpha Testing

This is an unsigned, portable development build. It is intended for workflow testing, not production work. Extract the entire zip before running `Compositor.App.exe`; Windows may show a SmartScreen warning because the build is not code-signed.

## Useful workflows

1. Create a canvas, add and reorder layers, then save and reopen the `.comp` project.
2. Import PNG, JPEG or WebP images; move, resize, rotate and crop them.
3. Create a rectangular selection, paint across its edge, undo/redo, erase, save and reopen.
4. Exercise masks, clipping masks, blend modes, PNG export and JPEG export.
5. Leave a modified project open for at least 45 seconds, end the process from Task Manager, restart, and verify recovery opens as an unsaved project without changing the original file.
6. Use Image → Canvas Size with several anchors and optional fill, then Image → Image Size with aspect lock and different sampling modes; undo and redo both operations.
7. Ctrl-click and Shift-click several layers, then group or delete them and verify the action is a single undo step.

Keep test documents backed up. PSD/PSB import and export are planned but are not implemented in this build.

## Reporting a problem

Include:

- What you expected and what happened.
- The shortest sequence that reproduces it.

Layer order is currently changed with the Up/Down buttons at the bottom of the Layers panel or with **Move Layer Up/Down** in a layer's context menu. Direct row drag-and-drop is not yet part of the supported test workflow.
- The `.comp` project or source image when it is safe to share.
- The newest diagnostic log. In Compositor choose **Help → Open Diagnostic Logs**, or open `%LOCALAPPDATA%\Compositor\Logs` manually.

Logs record tool changes, commands, document edits, selection and paint gestures, local file paths used for project I/O, runtime details, and exceptions. They do not record image pixel contents. The newest 20 run logs are retained automatically.

Crash-recovery files live under `%LOCALAPPDATA%\Compositor\Recovery`. Preserve that folder alongside the log when reporting a recovery failure; it may contain the document pixels being recovered, so do not share it publicly without reviewing the content.
