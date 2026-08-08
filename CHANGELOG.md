# Changelog

## [0.2.0] - 2026-08-06

UI Toolkit support, play-mode transitions, and a fix that made the existing patches applicable at all.

### Commands
- `uitk_find` — locate UI Toolkit elements by name, USS class or text, across every runtime panel
- `uitk_tree` — print the element tree, walking `hierarchy` so a control's generated parts are visible
- `uitk_style` — resolved style and layout box, measured after the panel has laid out
- `play_mode` — enter or leave play mode and return once the transition completed

The `ui_*` commands address uGUI. A UI Toolkit element is not a GameObject and never appears in the
scene hierarchy, so on a UI Toolkit project `ui_find` and `get_scene_hierarchy` both report the entire
UI as a handful of empty `UIDocument` objects.

### Patches
- `eval_usings` — `eval` compiles against `UnityEngine.UIElements`, `UnityEngine.SceneManagement`,
  `System.Text` and `System.Reflection`. Without it UI Toolkit code must be fully qualified, and
  extension methods such as `Q()` fail with `CS1061` because the wrapper body cannot declare usings.
- `recompile_playmode_guard` — `recompile` refuses while playing unless `force=true`. A reload during
  play resets every static field while the scene and the reported playMode stay unchanged.

### Client
- `Tools~/upipe` — the shell client is now part of the package, with `install.sh`
  (macOS/Linux/WSL) and `install.bat` (Windows, via WSL or Git Bash). It resolves the
  project by walking up from the working directory, so one install serves every project.

### Fixed
- **Anchor matching ignored line endings, so every patch reported `AnchorNotFound`.** The anchors are
  C# verbatim strings carrying this repository's checkout endings, while the package source carries
  whatever Unity wrote into `PackageCache`. A CRLF checkout against an LF package cache mismatched
  every multi-line anchor on its first newline — including all three 0.1.0 patches, which were
  therefore unapplicable on Windows. Matching now normalizes both sides to LF and writes the file back
  with the endings it already used.

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
