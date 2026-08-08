# More Upipe

Extra commands and opt-in patches for [`com.unity.pipeline`](https://docs.unity3d.com/Packages/com.unity.pipeline@0.4/manual/index.html).

The Pipeline package lets a CLI or coding agent drive a running Unity Editor over HTTP. It ships ~140 commands, but nothing for the questions that dominate UI work — *which object is this, is it on, why isn't my click landing, why is my input ignored* — and its console command returns the whole buffer, which an agent pays for in full before discarding any of it.

This package fills those gaps with ten commands and five patches.

| Command | What it answers |
|---|---|
| `ui_find` | Where is this object, is it active — **including inactive objects** |
| `ui_tree` | What is under this object, and which branch is switched off |
| `ui_click` | Click a UI object, working around the legacy input module |
| `ui_input` | Set an input field's text **and fire its change callbacks** |
| `ui_raycast` | What sits under this screen point — i.e. what is eating the click |
| `uitk_find` | The same questions for **UI Toolkit**, which the above cannot reach |
| `uitk_tree` | What is under this element, including a control's generated parts |
| `uitk_style` | Resolved style and layout box, **measured after the panel lays out** |
| `wait_for` | Block until a condition holds, instead of polling from the client |
| `play_mode` | Enter or leave play mode and return once it actually happened |

| Patch | Effect |
|---|---|
| `console_filter` | Server-side `grep` and stack-trace suppression — measured 32 KB → 294 B |
| `quiet_unknown_command` | A typo no longer echoes every command name back |
| `status_frame_count` | `editor_status` reports `frameCount`, which reveals domain reloads |
| `eval_usings` | `eval` compiles against UI Toolkit, so `Q()` and friends resolve |
| `recompile_playmode_guard` | `recompile` refuses to silently wipe static state mid-play |

The `ui_*` commands address uGUI. A UI Toolkit element is not a GameObject, carries no component and never appears in the scene hierarchy, so on a UI Toolkit project `ui_find` and `get_scene_hierarchy` both report the entire UI as a handful of empty `UIDocument` objects. The `uitk_*` commands are the counterpart set.

## Why bother

An agent driving the Editor pays twice for every question: once to phrase it, once to read the answer. Both costs are measurable, and both are larger than they need to be.

The numbers below were taken on one project (a UI Toolkit game, Unity 6000.3.18f1, `com.unity.pipeline` 0.4.0-exp.1) with the Editor in play mode and the same panel open for each run. They are one machine and one project, not a benchmark suite — but the shape holds anywhere the same questions get asked.

### Asking costs less

Locating a UI Toolkit element through stock `eval` means sending the query as C# source, every time:

```csharp
var docs = UnityEngine.Object.FindObjectsByType<UnityEngine.UIElements.UIDocument>(
    UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None);
foreach (var d in docs) {
    var e = UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.VisualElement>(
        d.rootVisualElement, "TitleLabel");
    ...
```

| Question | Stock (`eval` source) | This package | Saved |
|---|---|---|---|
| Find one element | 753 B | `uitk_find name=TitleLabel` — 25 B | **97%** |
| Dump a subtree | 1,329 B | `uitk_tree name=DetailPanel depth=3 size=true` — 48 B | **96%** |

The C# is also written fresh each time, so it carries the risk of being wrong — and a compile error costs another round-trip before any answer arrives.

### Answers cost less

| Query | Stock | This package | Saved |
|---|---|---|---|
| Console, 40 entries, ignoring a known-noisy warning | 32,373 B | 294 B | **99%** |
| Mistyped command name | 2,933 B | 293 B | **90%** |
| Find one element | 592 B | 928 B | *−57%* |
| Dump a subtree | 909 B | 991 B | *−9%* |

The last two are **larger**, deliberately: `uitk_find` also reports the USS class list, which ancestor hid the element, and how many documents it searched. Getting the same out of `eval` means a longer query or a second call.

The console figure is the one that matters most, because it is paid on every log query. During measurement the project's buffer turned out to be filled by a per-frame warning:

```
Renderer at <b>index 2</b> is missing for camera <b>[MainCamera]</b>, ...
```

All forty entries were that one message. Stock, the only way to look past it is to fetch all 32 KB and filter locally — which for an agent means the whole payload has already been read before any of it is discarded.

| Console query | Bytes |
|---|---|
| unfiltered | 32,373 |
| `stack=false` | 13,173 |
| `stack=false` + `grep_not="Renderer at"` | 294 |

### Waiting costs less

| Operation | Stock | This package |
|---|---|---|
| Find one element | 432 ms | 46 ms |
| Dump a subtree | 450 ms | 29 ms |
| Leave play mode | ~35,000 ms (fixed sleep) | 1,040 ms |

The first two are faster because stock `eval` compiles through Roslyn on every call, while a registered command is already compiled.

The third is not really a speed difference. `editor_stop` returns before the transition happens, so the caller cannot tell when it is safe to continue and covers the worst case with a fixed sleep. The transition itself takes about a second; the other thirty-four are insurance against not knowing. `play_mode` returns when the state is actually there.

### Things that are not slow, just impossible

Some gaps do not show up as bytes or milliseconds because stock has no answer at all.

- **`eval` cannot see UI Toolkit.** The wrapper supplies five usings, none of them `UnityEngine.UIElements`, and the code is placed inside a method body so the caller cannot add one. `VisualElement`, `UIDocument` and `DisplayStyle` all fail with `CS0246`. → `eval_usings`
- **Layout is one frame behind.** A measurement taken in the same call that changed a style reads stale — the fix is to issue the query twice and trust the second answer. → `uitk_style` waits
- **A recompile during play silently resets every static.** The scene keeps running and `editor_status` still reports `playing`, so nothing anywhere says a domain reload happened; the failure surfaces later in whatever depended on that state. → `recompile_playmode_guard`, and `status_frame_count` to detect it after the fact

### Honest caveats

- Two of the four response sizes got **bigger**, for the reason given above. If payload size is the only thing being optimised, `uitk_find`'s extra fields are not free.
- The console and unknown-command figures depend on how noisy the project is. A quiet console has less to filter.
- The 35-second figure is what a caller *chooses* when it cannot know; a project with Reload Domain switched off can wait on both directions properly and would see a smaller gap.
- Everything here is one project on one machine.

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

### The `upipe` client (optional)

`Tools~/upipe` is a shell client for the Pipeline HTTP server. It is optional — every command here works through whatever client you already use — but it exists because the stock `unity` CLI cannot reach an editor across the WSL boundary: it validates the editor's process id, WSL cannot see a Windows pid, so it decides the descriptor is stale and deletes it. Talking to the HTTP server directly crosses that boundary without trouble.

The folder is named `Tools~`, so Unity ignores it: a shell script is not an asset and should not be imported, given a `.meta`, or compiled.

```sh
# macOS / Linux / WSL
./Tools~/install.sh                 # installs to ~/.local/bin
./Tools~/install.sh /usr/local/bin  # or somewhere else
./Tools~/install.sh --uninstall
```

```bat
:: Windows — installs into WSL if present, otherwise Git Bash
Tools~\install.bat
Tools~\install.bat --uninstall
```

Needs `curl` and `python3`; the installer checks for both before copying anything. On Windows the script itself runs under WSL or Git Bash, not `cmd`.

No configuration: `upipe` walks up from the working directory looking for `Library/Pipeline/.unity-pipeline-port`, so one install serves every project. `UPIPE_PROJECT` overrides that when you need to point at a project you are not standing in.

```sh
upipe status                       # server heartbeat
upipe commands [filter]            # list command names
upipe describe <name>              # parameters for one command
upipe exec <cmd> [json | k=v ...]  # run any command; prints the result only
upipe eval - <<'EOF'  ...  EOF     # C# from stdin, no quoting rules
upipe rc [force]                   # recompile, refusing while playing
upipe log [sev] [n] [grep]         # console; UPIPE_STACK=1 adds stack traces
upipe play | stop                  # uses play_mode, so it waits for the transition
```

Two of these lean on what this package adds: `upipe rc` refuses during play whether or not `recompile_playmode_guard` is applied, and `upipe play`/`stop` use `play_mode` when it is installed and fall back to `editor_play`/`editor_stop` with a warning when it is not.

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

### `uitk_find`

Find UI Toolkit elements by name, USS class or text, across every runtime panel.

```bash
unity command uitk_find --name TitleLabel
unity command uitk_find --class detail-action --document SettingsPopup
unity command uitk_find --text "Apply" --limit 5
```

| Parameter | Default | Meaning |
|---|---|---|
| `name` | — | Element name. Substring unless `exact` |
| `class` | — | USS class the element must carry. Always an exact class-list test |
| `text` | — | Visible text. Substring unless `exact` |
| `document` | — | Restrict to documents whose GameObject name contains this |
| `exact` | `false` | Require exact matches for `name` and `text` |
| `limit` | `30` | Maximum results |

`class` is exact rather than substring because USS class names are a fixed vocabulary; a substring match on them mostly returns modifiers of unrelated blocks.

```json
{
  "count": 1, "truncated": false, "documentsSearched": 6,
  "results": [
    { "document": "SettingsPopup",
      "path": "GamePanelSettings/…/DetailPanel/DetailScroll/…/DetailBody/TitleLabel",
      "name": "TitleLabel", "type": "Label",
      "classes": "unity-text-element unity-label detail-name",
      "text": "Sample value", "display": "Flex",
      "width": 296.93, "height": 32.99, "enabled": true, "hiddenBy": null }
  ]
}
```

`hiddenBy` names the ancestor that switched the element off. `display: none` propagates, so an element can be correct in every respect and still be absent because a parent is hidden — and the element's own `display` reads `Flex` the whole time.

Panels are reached through `UIDocument`, which is public API, rather than the internal panel registry. Inactive documents are included: "the panel is switched off" is one of the answers being looked for, and excluding them would report it as *not found*.

### `uitk_tree`

Print the element tree under a panel or a named element.

```bash
unity command uitk_tree --name DetailPanel --depth 3 --size true
```

| Parameter | Default | Meaning |
|---|---|---|
| `name` | — | Root element name. Omit for the whole panel |
| `document` | — | Restrict to documents whose GameObject name contains this |
| `depth` | `4` | Levels below the root |
| `classes` | `false` | Show the USS class list beside each entry |
| `size` | `false` | Show resolved width × height beside each entry |
| `max_nodes` | `400` | Stop after this many entries |

```
# SettingsPopup
[ ] DetailPanel<VisualElement>  338x899
  [-] EmptyState<VisualElement>  0x0
    [~] EmptyLabel<Label>  "Nothing selected"  0x0
  [ ] DetailScroll<ScrollView>  302x806
    [ ] unity-content-and-vertical-scroll-container<VisualElement>  302x806
      [ ] unity-content-viewport<VisualElement>  302x806
    [-] Scroller  0x0
      [-] unity-low-button<RepeatButton>  0x0
      [-] unity-high-button<RepeatButton>  0x0
  [ ] DetailActions<VisualElement>  302x49
    [ ] ApplyButton<Button>  "Apply"  146x49
    [ ] CancelButton<Button>  "Cancel"  146x49
```

Markers match `ui_tree`: `[-]` the element itself is hidden, `[~]` the element is fine but an ancestor is not. Only the first is fixed on the element.

The walk uses `VisualElement.hierarchy`, not `Children()`. A composite control keeps its generated pieces outside the content container — the checkmark a `Toggle` attaches, the `Scroller` a `ScrollView` owns — and those pieces are usually the answer when a widget renders wrong. The listing above shows a `ScrollView` whose scroller buttons were hidden by USS, which `Children()` would not have revealed.

### `uitk_style`

Read one element's resolved style and layout box.

```bash
unity command uitk_style --name DetailScroll --props display,flexGrow,width,height
```

| Parameter | Default | Meaning |
|---|---|---|
| `name` | **required** | Element name to resolve |
| `document` | — | Restrict to documents whose GameObject name contains this |
| `props` | — | Comma-separated property names. Omit for the common layout set |
| `await_frames` | `1` | Frames to let pass before measuring. `0` measures immediately |

**Why the wait is part of the command:** UI Toolkit resolves layout on the next frame, so a value read in the same call that changed a style, added a class or toggled `display` is stale — usually zero or `NaN`. Through `eval` this surfaces as a measurement that disagrees with the screen, and the workaround is to issue the same query twice and trust the second answer. Waiting here makes the first answer the correct one, and removes a class of "the layout is broken" reports that were only a measurement taken too early.

```json
{ "document": "SettingsPopup", "path": "…/DetailPanel/DetailScroll",
  "type": "ScrollView", "classes": "unity-scroll-view … detail-scroll",
  "worldBound": { "x": 302.4, "y": 222.7, "width": 302.4, "height": 805.6 },
  "hiddenBy": null, "awaitedFrames": 1,
  "style": { "display": "Flex", "position": "Relative",
             "width": 302.4, "height": 805.6, "flexGrow": 1.0 } }
```

`resolvedStyle` is what the style system computed; `worldBound` is where the element actually sits. They disagree when a parent clips, scales or shrinks it, and that disagreement is usually the bug — so both are reported rather than one standing in for the other.

Ambiguous names are refused with the list of candidates, for the same reason as `ui_click`: element names repeat across documents, and measuring the wrong one produces a confident answer about the wrong element.

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

### `play_mode`

Enter or leave play mode and return once the transition has happened.

```bash
unity command play_mode --state stop
unity command play_mode --state play --settle false
```

| Parameter | Default | Meaning |
|---|---|---|
| `state` | **required** | `play` or `stop` |
| `settle` | `true` | Wait for the transition. `false` returns as soon as it is requested |
| `timeout` | `60` | Seconds to wait before giving up |

**Why this exists next to `editor_play` / `editor_stop`:** those set `EditorApplication.isPlaying` and return in the same call, but Unity applies the change at the end of the frame. The visible symptom is a pair of responses that contradict each other — `editor_stop` answers *Exited play mode*, and the `editor_play` issued right after answers *Already in play mode*, because the exit had not landed when the second command read the flag. The usual client-side fix is a fixed sleep sized for the worst case: dead time on every call, and still a guess.

Leaving play mode settles quickly and is reported exactly:

```json
{ "changed": true, "playMode": "stopped", "settled": true, "elapsedSeconds": 1.04 }
```

Entering is different. A domain reload tears down the HTTP server along with the pending request, so the answer can never arrive. Rather than hold a connection that is about to be severed and hand back a transport error to interpret, the command reads the project's Enter Play Mode Options and says so:

```json
{ "changed": true, "requested": "playing", "settled": false, "reason": "domain reload",
  "detail": "Entering play mode reloads the domain, which drops this connection before it can answer. …" }
```

Play mode still starts. Confirm with `editor_status`, or `wait_for` a condition in the running scene — which is the better wait anyway, since a scene that has entered play is usually not yet ready. With **Reload Domain** switched off in Project Settings, `settle` works in both directions.

Requesting the state the Editor is already in is not an error; it returns `changed: false` with the current state.

## Patches

The commands above cover what can be *added*. Some things can only be fixed by changing an existing command — those ship as opt-in patches applied to the installed pipeline package.

Open **Tools > More Upipe > Patch Manager**, review, tick, apply. Nothing is applied automatically; a console line notes when patches are pending.

| Patch | Files | What changes |
|---|---|---|
| `console_filter` | `Runtime/Commands/ConsoleCommand.cs` | Adds `grep`, `grep_not` and `stack` to `console` |
| `quiet_unknown_command` | `Runtime/Common/BasePipelineServer.cs` | A mistyped command name no longer echoes every registered name |
| `status_frame_count` | `Editor/Commands/EditorStatusCommand.cs`, `Runtime/Models/StatusResponse.cs` | Adds `frameCount` to `editor_status` |
| `eval_usings` | `Runtime/Compilation/EvalCodeCompiler.cs` | `eval` compiles against `UnityEngine.UIElements` and three more namespaces |
| `recompile_playmode_guard` | `Editor/Commands/RecompileCommand.cs` | `recompile` refuses while playing unless `force=true` |

**`eval_usings`.** The wrapper compiles user code against `System`, `System.Collections.Generic`, `System.Linq`, `UnityEngine` and — in the Editor — `UnityEditor`. Everything else must be written fully qualified, and because the code is placed inside a method body the caller cannot add a using of their own. UI Toolkit suffers most: its API is extension-method heavy, so `root.Q<Label>("Name")` fails with `CS1061` — an error that reads like the type is wrong rather than the using is missing — and the working form is `UnityEngine.UIElements.UQueryExtensions.Q<Label>(root, "Name")`. The patch adds `UnityEngine.UIElements`, `UnityEngine.SceneManagement`, `System.Text` and `System.Reflection`.

Only Unity namespaces are added. Project namespaces are deliberately left out: they differ per project, and a using for a namespace that does not exist is a compile error on *every* eval — a convenience turned into a breakage.

**`recompile_playmode_guard`.** A recompile during play triggers a domain reload. The scene survives, so the objects are still there and `editor_status` still reports playing — but every static field has been reset. A singleton read through `Instance` returns null while `FindObjectsByType` returns the very object it should have handed back, and nothing in any response says a reload happened. The failure surfaces later, in whatever command depended on that state.

`status_frame_count` makes the reload detectable after the fact; this makes the common cause avoidable. Refused calls answer plainly:

```json
{ "status": "refused", "reason": "editor is playing",
  "detail": "Recompiling now triggers a domain reload that resets all static state while the scene keeps running. Stop play mode first, or pass force=true." }
```

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
- Matching normalizes line endings to LF on both sides, and the patched file is written back with whatever endings it already used. The anchors live in C# verbatim strings, so they carry this repository's checkout endings, while the package source carries whatever Unity wrote into `PackageCache`. Without normalizing, a CRLF checkout against an LF package cache mismatches every multi-line anchor on its first newline — and reports it as `AnchorNotFound`, indistinguishable from the package genuinely having changed.
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
