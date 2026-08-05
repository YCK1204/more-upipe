# More Upipe

Extra commands and opt-in patches for [`com.unity.pipeline`](https://docs.unity3d.com/Packages/com.unity.pipeline@0.4/manual/index.html).

The Pipeline package lets a CLI or coding agent drive a running Unity Editor over HTTP. It ships ~140 commands, but nothing for the questions that dominate UI work — *which object is this, is it on, why isn't my click landing, why is my input ignored* — and its console command returns the whole buffer, which an agent pays for in full before discarding any of it.

This package fills those gaps with six commands and three patches.

| Command | What it answers |
|---|---|
| `ui_find` | Where is this object, is it active — **including inactive objects** |
| `ui_tree` | What is under this object, and which branch is switched off |
| `ui_click` | Click a UI object, working around the legacy input module |
| `ui_input` | Set an input field's text **and fire its change callbacks** |
| `ui_raycast` | What sits under this screen point — i.e. what is eating the click |
| `wait_for` | Block until a condition holds, instead of polling from the client |

| Patch | Effect |
|---|---|
| `console_filter` | Server-side `grep` and stack-trace suppression — measured 49 KB → 13 KB |
| `quiet_unknown_command` | A typo no longer echoes every command name back |
| `status_frame_count` | `editor_status` reports `frameCount`, which reveals domain reloads |

## Install

Unity Package Manager → **Add package from git URL**:

```
https://github.com/YCK1204/more-upipe.git
```

Or add to `Packages/manifest.json`:

```json
"com.yck1204.more-upipe": "https://github.com/YCK1204/more-upipe.git"
```

Requires `com.unity.pipeline` and `com.unity.ugui`, Unity 6.0 or newer. Editor-only — the assembly is excluded from builds, and on older Unity versions it is excluded from compilation entirely rather than failing to build.

## Commands

### `ui_find`

Find scene objects by name or visible label. Inactive objects are included, which is the entire point: `GameObject.Find` skips them, and "which object got switched off" is the usual question.

```bash
unity command ui_find --name MenuBtn
unity command ui_find --label "Play" --exact true
unity command ui_find --name Btn --components true --limit 50
```

| Parameter | Default | Meaning |
|---|---|---|
| `name` | — | Object name. Substring unless `exact` |
| `label` | — | Visible text (`UI.Text` or TextMeshPro). Substring unless `exact` |
| `exact` | `false` | Require an exact match |
| `limit` | `30` | Maximum results |
| `components` | `false` | Include component type names |

At least one of `name` or `label` is required; supplying both requires the object to satisfy each.

```json
{
  "count": 1,
  "truncated": false,
  "results": [
    { "path": "Canvas/Panel/MenuBtn", "name": "MenuBtn",
      "activeSelf": false, "activeInHierarchy": false, "label": "Menu", "components": null }
  ]
}
```

Label matching reads text components on the object and its **direct children only**. Searching the whole subtree would make every ancestor canvas match its descendants' labels, burying the actual button in noise.

TextMeshPro is resolved by type name through reflection, so this compiles with or without TMP present.

### `ui_tree`

Print the hierarchy under one object.

```bash
unity command ui_tree --name LoginScreen --depth 2
```

| Parameter | Default | Meaning |
|---|---|---|
| `path` / `name` | — | Root of the subtree. `path` wins when both are given |
| `depth` | `4` | Levels below the root |
| `labels` | `true` | Show visible text beside each entry |
| `max_nodes` | `400` | Stop after this many entries |

```
[ ] LoginScreen
  [-] BgCover
  [ ] ID_Input
    [ ] Placeholder
    [ ] Text  "AB12"
  [ ] LoginButton
  [-] LoginInstructionsHeader
    [~] LoginInstructionsTxt  "Choose a login method."
```

`[-]` the object itself is disabled. `[~]` the object is enabled but an ancestor is not — the case that tends to be missed. Output is indented text rather than nested JSON because a tree is read by a human or a model, and indentation carries the structure at a fraction of the tokens repeated JSON keys would cost.

`get_scene_hierarchy` covers the whole scene; this scopes to the part being debugged.

### `ui_click`

Click a UI object by sending pointer events directly to it.

```bash
unity command ui_click --path "Canvas/LoginScreen/LoginButton"
unity command ui_click --name LoginButton
```

**Why this instead of `simulate_pointer`:** that command feeds the Input System, which a project running the legacy `StandaloneInputModule` ignores outright — the event is dispatched and nothing happens. Sending pointer down / up / click to the target works regardless of input backend. Down and up are included because many widgets latch state on pointer-down and only commit on the click.

```json
{ "path": "Canvas/LoginScreen/LoginButton", "handled": true,
  "pointerDown": true, "pointerUp": true, "pointerClick": true }
```

`handled: false` means the object carries no pointer handler — useful to distinguish "clicked" from "clicked nothing".

Two things it refuses to do:

- **Ambiguous names are reported, not guessed.** UI names repeat across canvases; silently clicking the wrong one is worse than not clicking. Pass an exact `path` instead.
- **Inactive targets are rejected.** Clicking what a player cannot see is a false positive that hides the bug.

**Known limitation:** dispatching directly bypasses the raycast, so a button covered by another graphic still "clicks" here even though a real user could not press it. That is what `ui_raycast` is for.

### `ui_input`

Set an input field's text.

```bash
unity command ui_input --path "Canvas/LoginScreen/ID_Input" --text "AB12"
```

| Parameter | Default | Meaning |
|---|---|---|
| `path` / `name` | — | Target field. `path` wins when both are given |
| `text` | **required** | Text to assign |
| `submit` | `true` | Also fire `onEndEdit` / `onSubmit` |

**Why assigning `.text` by hand is not enough:** a field whose backing string is replaced never raises `onValueChanged` or `onEndEdit`, so validation, submit buttons and view-model bindings never learn the value changed. The text sits on screen and the game behaves as if the field were empty.

Both `UnityEngine.UI.InputField` and TextMeshPro's `TMP_InputField` are handled; the response reports which one it found. Ambiguous names are refused for the same reason as `ui_click`.

### `ui_raycast`

List everything the UI raycaster hits at a screen point, topmost first.

```bash
unity command ui_raycast --x 960 --y 540
```

This is the diagnostic for a click that goes nowhere. The input module dispatches to the first hit, so a full-screen cover, a loading overlay or a stray raycast-enabled image shows up here as entry zero.

```json
{ "point": { "x": 960.0, "y": 540.0 }, "count": 2,
  "hits": [ { "path": "Canvas_Dialog/ClickCheckPanel", "sortingOrder": 3100, "depth": 6,
              "module": "GraphicRaycaster" } ] }
```

Coordinates are in pixels with the origin at the bottom-left.

### `wait_for`

Poll a C# expression on the server until it matches, then return.

```bash
unity command wait_for \
  --code 'return MyGame.GameSystem.Instance?.CurrentView.ToString();' \
  --equals Lobby --timeout 180 --interval 3
```

| Parameter | Default | Meaning |
|---|---|---|
| `code` | **required** | Expression body evaluated each attempt. Must return a value |
| `equals` | — | Succeed when the result equals this string |
| `contains` | — | Succeed when the result contains this substring |
| `timeout` | `60` | Seconds before giving up |
| `interval` | `1` | Seconds between attempts (floored at 0.1) |

With neither `equals` nor `contains`, any result that is non-empty and not `"null"`/`"false"` counts as success — the common "wait until this stops being null" case.

```json
{ "succeeded": true, "value": "Lobby", "attempts": 45, "elapsedSeconds": 161.59 }
```

A timeout returns `succeeded: false` with the last observed value rather than raising an error — a wait that ran out is a result, and the last value usually explains why.

**Why it matters:** without this the caller polls, and every round-trip is a request/response pair the agent has to read. Waiting for a login to reach the lobby took 45 attempts in testing; as a client-side loop that is 45 responses, here it is one.

Evaluation runs on the main thread through the Pipeline package's own eval facility, so anything expressible in `eval` works, including project types. The command is declared `MainThreadRequired = false` so the delay between attempts does not block the Editor.

## Patches

The commands above cover what can be *added*. Some things can only be fixed by changing an existing command — those ship as opt-in patches applied to the installed pipeline package.

Open **Tools > More Upipe > Patch Manager**, review, tick, apply. Nothing is applied automatically; a console line notes when patches are pending.

| Patch | Files | What changes |
|---|---|---|
| `console_filter` | `Runtime/Commands/ConsoleCommand.cs` | Adds `grep`, `grep_not` and `stack` to `console` |
| `quiet_unknown_command` | `Runtime/Common/BasePipelineServer.cs` | A mistyped command name no longer echoes every registered name |
| `status_frame_count` | `Editor/Commands/EditorStatusCommand.cs`, `Runtime/Models/StatusResponse.cs` | Adds `frameCount` to `editor_status` |

Measured on one 40-entry console query:

| Query | Bytes |
|---|---|
| unfiltered | 49,309 |
| `stack=false` | 24,720 |
| `stack=false` + `grep` | 13,077 |

The unknown-command error dropped from roughly 2 KB to a single line. `frameCount` matters because `editor_status` keeps reporting playMode `playing` after a domain reload has restarted the scene — the frame counter resets, so a value far below the last one means the world was rebuilt.

### How it works

Each patch is one or more anchors plus replacements, not a diff against a known revision:

- An anchor must occur **exactly once** in its file. Zero means the package source moved (or the patch is already applied); more than one means the anchor is not specific enough to place safely. Both refuse rather than guess.
- A patch may span several files — `status_frame_count` edits a command and its response model, and neither compiles without the other. They apply together or not at all, so "applied one half" is unrepresentable rather than merely discouraged.
- An applied patch leaves a marker comment (`// [MoreUpipe:<id>]`). That marker — read from the file, not from stored state — is the answer to "is this applied?".
- The original file is copied to `<file>.moreupipe.orig` before the first write, and **Revert** restores it.
- Nothing is written unless *every* selected patch is applicable.

### Things worth knowing before you apply

- **Patched files live in `Library/PackageCache`, which Unity regenerates.** A package re-resolve silently restores the originals. That is why the patch state is re-checked each session and reported in the console — re-open the window and apply again.
- **Patches are tied to a package version.** These anchors were taken from `com.unity.pipeline` 0.4.0-exp.1. On a different version an anchor may not match, in which case the patch reports `AnchorNotFound` and changes nothing. The commands themselves use only public API and are unaffected.
- **The alternative was embedding the whole package** under `Packages/`, which makes your project own 12 MB of third-party source and turns every package update into a manual merge. Re-applying a small, self-describing edit is the cheaper trade.

## Notes

- Verified against `com.unity.pipeline` **0.4.0-exp.1** on Unity **6000.0.69f1**. That package is experimental and its API can change without notice — if a command stops registering after a package update, that is the first thing to check.
- New `.cs` files are not picked up by a recompile alone. Trigger an asset import first (`Assets/Refresh`), then recompile.
- Commands are discovered automatically via `TypeCache`; there is no registration step.
- The pipeline `eval` command caps `timeout` at 30000 ms. Larger values fail with a bare `Bad Request` whose real reason only appears in `errorDetails`.

## License

MIT. See [LICENSE](LICENSE).
