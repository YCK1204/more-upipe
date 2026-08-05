# Changelog

## [0.1.0] - 2026-08-05

Initial release.

### Commands
- `ui_find` — locate scene objects by name or visible label, inactive included
- `ui_tree` — print the hierarchy under an object, marking disabled branches
- `ui_click` — click UI through `ExecuteEvents`, bypassing the input-backend mismatch
- `ui_input` — set an InputField / TMP_InputField's text and fire its change callbacks
- `ui_raycast` — list what the UI raycaster hits at a screen point
- `wait_for` — server-side polling of a C# condition

### Patches
- `console_filter` — `grep`, `grep_not` and `stack` parameters for `console`
- `quiet_unknown_command` — a mistyped command name no longer returns every command name
- `status_frame_count` — `frameCount` in `editor_status`, so domain reloads are detectable

Verified against `com.unity.pipeline` 0.4.0-exp.1 on Unity 6000.0.69f1.
