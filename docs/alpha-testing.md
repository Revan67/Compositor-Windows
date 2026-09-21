# Compositor Windows — Alpha Testing

This is an unsigned, portable development build. It is intended for workflow testing, not production work. Extract the entire zip before running `Compositor.App.exe`; Windows may show a SmartScreen warning because the build is not code-signed.

## Useful workflows

1. Create a canvas, add and reorder layers, then save and reopen the `.comp` project.
2. Import PNG, JPEG or WebP images; move, resize, rotate and crop them.
3. Create a rectangular selection, paint across its edge, undo/redo, erase, save and reopen.
4. Exercise masks, clipping masks, blend modes, PNG export and JPEG export.

Keep test documents backed up. PSD/PSB import and export are planned but are not implemented in this build.

## Reporting a problem

Include:

- What you expected and what happened.
- The shortest sequence that reproduces it.
- The `.comp` project or source image when it is safe to share.
- The newest diagnostic log. In Compositor choose **Help → Open Diagnostic Logs**, or open `%LOCALAPPDATA%\Compositor\Logs` manually.

Logs record tool changes, commands, document edits, selection and paint gestures, local file paths used for project I/O, runtime details, and exceptions. They do not record image pixel contents. The newest 20 run logs are retained automatically.
