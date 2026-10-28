# Changelog

## [0.3.0] - 2026-10-28

Support for `com.unity.pipeline` 0.9, a `qa` command group for the after-every-change loop, grouped
command names, and the Unity CLI as the only client.
Requires `com.unity.pipeline` 0.9.1-exp.1 or newer; stay on `v0.2.0` for 0.7 and older (0.8 is untested).

### Commands
- `qa check` — Editor state, compile errors, console totals and the newest distinct errors in one call.
- `qa log` — the Editor console filtered on the server (`level`, `grep`/`grep_not` with `|` alternatives,
  `since` cursor), repeats collapsed into one line with a `count`, stack traces off unless asked.
  Reads `UnityEditor.LogEntries`, so compile errors are included and the Console window's level
  toggles do not hide anything.
- `qa tests` — run tests and answer with counts and failures only (one-line message and
  `Assets/…:line` each). Exits non-zero on a failure, a timeout or no match unless `strict=false`.
  Every run is numbered and the command only accepts its own run's result. `failed=true` runs the
  tests known to be failing.
- `qa test_result` — the last finished run's summary, recorded in `SessionState`, so the result of a
  run whose connection a domain reload dropped can still be read.
- `qa test_list` — test inventory grouped by fixture with counts.
- Measured on a 2,100-test project: console errors 10,058 → 92 tokens, a passing fixture 523 → 32, the
  whole suite's failures 44,519 (cut to 6 of 20) → 1,900 (all 20), the test inventory 149,058 → 1,421.

### Changed
- **Command names are grouped**, the way Pipeline 0.9 names its own: `ugui find|tree|click|input|raycast`,
  `uitk find|tree|style`, `wait until`, `wait playmode` (were `ui_*`, `uitk_*`, `wait_for`, `play_mode`).
- `com.unity.test-framework` is a dependency (for the `qa` test commands).
- The obsolete `FindObjectsByType` sort-mode overload is called from one place with the warning
  silenced, so the package compiles without `CS0618` warnings on Unity 6.x.

### Fixed
- **The package did not compile on Pipeline 0.8+.** `[CliCommand]` and `[CliArg]` moved to the
  `Unity.Pipeline.Attributes` assembly (UUM-153151), so every command failed with `CS0246`. The asmdef
  now references it.
- **`wait until` (was `wait_for`) did not compile on Pipeline 0.8+.** It called `CodeEvalCommand.EvaluateCode`, which is
  internal. It now finds the `script eval` command through the public `CommandRegistry` and invokes it.
- **`wait until` and `wait playmode` never reported their own timeout.** Since Pipeline 0.9 a `timeout`
  parameter is the request timeout and the server answers 504 when it runs out. Both now take it as
  `double`, read the time left from it, and stop up to one second early to answer `reason: "timeout"`.

### Patches
- `console_filter` — anchors rewritten for 0.9.1 (`Get` with `since_session` and `GroundTruth`). With a
  text filter it now searches the whole buffer and keeps the newest `tail` matches, so one noisy
  warning can no longer push every match out of the window.
- Removed `quiet_unknown_command` — Pipeline 0.9 suggests one close name on a typo (UUM-154017).
- Removed `status_frame_count` — Pipeline 0.9 reports `frameCount` in `editor status` itself; applying
  the patch now fails with `CS0102`.
- Anchors are verified against 0.9.1-exp.1 (`VerifiedPipelineVersion`).

### Removed
- `Tools~/upipe` and its installers. Use `unity command <name>` from the Unity CLI.

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
