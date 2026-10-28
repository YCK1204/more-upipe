using System.Collections.Generic;

namespace MoreUpipe.Patching
{
    /// <summary>
    /// The patch set. Every entry names the files in <c>com.unity.pipeline</c> it touches, the exact
    /// snippets it attaches to, and the text that replaces them.
    ///
    /// Patches are limited to changes that cannot be expressed as a new command. Anything that can
    /// live alongside the package ships as an added command instead — a new name never breaks someone
    /// else's automation, while an edit to an existing command changes behaviour for every caller in
    /// the project.
    /// </summary>
    public static class PipelinePatchCatalog
    {
        public static readonly IReadOnlyList<PipelinePatch> All = new[]
        {
            ConsoleFilter,
            EvalUsings,
            RecompilePlayModeGuard,
        };

        /// <summary>
        /// Server-side filtering for the <c>console</c> command: text match plus optional stack-trace
        /// suppression.
        ///
        /// Without it the whole buffer crosses the wire and the caller filters locally — which for an
        /// agent means the entire payload lands in its context first, and it has already been paid for
        /// by the time anything is discarded. A 40-entry response measured 48 KB unfiltered and 67
        /// bytes with a <c>grep</c>. Stack traces are the other half of the problem: one warning with a
        /// trace outweighs a dozen plain messages, so <c>stack=false</c> drops them.
        /// </summary>
        public static PipelinePatch ConsoleFilter => new PipelinePatch(
            id: "console_filter",
            title: "console: server-side text filter and stack-trace suppression",
            description: "Adds 'grep', 'grep_not' and 'stack' parameters. Matching happens in the Editor instead of shipping " +
                         "the whole buffer to the caller — a 40-entry response measured 48 KB unfiltered, 67 bytes filtered.",
            new PatchEdit(
                relativePath: "Runtime/Commands/ConsoleCommand.cs",
                anchor:
@"        public static ConsoleLogResponse Get(
            [CliArg(""tail"", ""Maximum number of most-recent entries to return"")] int tail = DefaultTail,
            [CliArg(""level"", ""Minimum severity to include: log | warn | error"")] string level = ConsoleLogBuffer.LevelLog,
            [CliArg(""since"", ""Cursor: only return entries newer than this seq. Use the 'cursor' from a previous response to follow."")] long since = -1,
            [CliArg(""since_session"", ""Session the 'since' cursor came from: pass the 'session' from a previous response. A cursor from another session is refused and the tail returned with reset=true."")] string since_session = null)
        {
            // Guard against nonsensical tail values; <= 0 would otherwise mean ""unlimited"" in the
            // buffer, which is not what a caller passing e.g. --tail 0 expects.
            if (tail <= 0)
                tail = DefaultTail;

            var minSeverity = ConsoleLogBuffer.SeverityFromLevelName(level);
            var response = ConsoleLogCapture.Buffer.Query(since, tail, minSeverity, since_session);
            response.GroundTruth = ReadGroundTruth();
            return response;
        }",
                replacement:
@"        public static ConsoleLogResponse Get(
            [CliArg(""tail"", ""Maximum number of most-recent entries to return"")] int tail = DefaultTail,
            [CliArg(""level"", ""Minimum severity to include: log | warn | error"")] string level = ConsoleLogBuffer.LevelLog,
            [CliArg(""since"", ""Cursor: only return entries newer than this seq. Use the 'cursor' from a previous response to follow."")] long since = -1,
            [CliArg(""since_session"", ""Session the 'since' cursor came from: pass the 'session' from a previous response. A cursor from another session is refused and the tail returned with reset=true."")] string since_session = null,
            [CliArg(""grep"", ""Only return entries whose message or stack trace contains this text (case-insensitive)."")] string grep = null,
            [CliArg(""grep_not"", ""Drop entries whose message or stack trace contains this text (case-insensitive)."")] string grepNot = null,
            [CliArg(""stack"", ""Include stack traces. Set false to strip them from the response."")] bool stack = true)
        {
            // Guard against nonsensical tail values; <= 0 would otherwise mean ""unlimited"" in the
            // buffer, which is not what a caller passing e.g. --tail 0 expects.
            if (tail <= 0)
                tail = DefaultTail;

            var hasTextFilter = !string.IsNullOrEmpty(grep) || !string.IsNullOrEmpty(grepNot);

            // A text filter reads the whole buffer and keeps the newest 'tail' matches. Cutting to
            // 'tail' first would let one noisy warning fill the window and leave nothing to match.
            var minSeverity = ConsoleLogBuffer.SeverityFromLevelName(level);
            var response = ConsoleLogCapture.Buffer.Query(since, hasTextFilter ? 0 : tail, minSeverity, since_session);
            response.GroundTruth = ReadGroundTruth();

            if (response?.Entries == null)
                return response;

            if (!hasTextFilter && stack)
                return response;

            var kept = new System.Collections.Generic.List<ConsoleLogEntry>();
            foreach (var entry in response.Entries)
            {
                if (entry == null)
                    continue;

                if (hasTextFilter)
                {
                    var haystack = (entry.Message ?? string.Empty) + ""\n"" + (entry.StackTrace ?? string.Empty);

                    if (!string.IsNullOrEmpty(grep) &&
                        haystack.IndexOf(grep, System.StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    if (!string.IsNullOrEmpty(grepNot) &&
                        haystack.IndexOf(grepNot, System.StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                }

                if (!stack)
                    entry.StackTrace = null;

                kept.Add(entry);
            }

            if (kept.Count > tail)
                kept.RemoveRange(0, kept.Count - tail);

            response.Entries = kept.ToArray();
            response.Returned = kept.Count;
            return response;
        }"));

        /// <summary>
        /// Widen the using directives that <c>eval</c> compiles user code against.
        ///
        /// The wrapper supplies System, System.Collections.Generic, System.Linq, UnityEngine and — in
        /// the Editor — UnityEditor. Everything else has to be written fully qualified, and because
        /// the wrapper puts the code inside a method body, the caller cannot add a using of their own.
        /// The result is that ordinary UI Toolkit work reads like
        /// <c>UnityEngine.UIElements.UQueryExtensions.Q&lt;Label&gt;(root, "Name")</c>, and the first
        /// attempt is usually <c>.Q&lt;Label&gt;(...)</c>, which fails with CS1061 because extension
        /// methods resolve through usings.
        ///
        /// UIElements is the namespace that costs the most to omit: every project with runtime UI needs
        /// it, and its API is extension-method heavy, so its absence produces errors that look like the
        /// type is wrong rather than the using is missing.
        ///
        /// Only namespaces shipped by Unity are added. Project namespaces are deliberately left out —
        /// they differ per project, and a using for a namespace that does not exist is a compile error
        /// on every eval, which would turn this from a convenience into a breakage.
        ///
        /// THE ALIASES ARE NOT OPTIONAL. Opening UIElements introduces three names that already exist
        /// in the namespaces the wrapper opened before it: <c>Cursor</c> and <c>PointerType</c> collide
        /// with UnityEngine, <c>PopupWindow</c> with UnityEditor. Without the aliases, code that
        /// compiled before the patch — <c>Cursor.visible</c> is the obvious one — starts failing with
        /// CS0104, and the caller has no way to disambiguate because they cannot write a using. Each
        /// alias pins the name to what it meant before, so this patch only ever adds reach; the
        /// UIElements versions stay available fully qualified.
        /// </summary>
        public static PipelinePatch EvalUsings => new PipelinePatch(
            id: "eval_usings",
            title: "eval: compile against UI Toolkit and common Unity namespaces",
            description: "Adds UnityEngine.UIElements, UnityEngine.SceneManagement, System.Text and System.Reflection to the " +
                         "eval wrapper. Without them UI Toolkit code must be fully qualified, and extension methods such as " +
                         "Q()/Query() fail with CS1061 because the caller cannot add a using of their own. Cursor, PointerType " +
                         "and PopupWindow are aliased to their pre-patch meaning so no code that compiled before stops doing so.",
            new PatchEdit(
                relativePath: "Runtime/Compilation/EvalCodeCompiler.cs",
                anchor:
@"            return $@""using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
{editorUsing}",
                replacement:
@"            // [MoreUpipe:eval_usings] UIElements carries the extension methods (Q/Query) that UI work
            // needs; the wrapper body cannot declare usings, so omitting it forces full qualification.
            // The aliases keep three names meaning what they meant before UIElements was opened —
            // Cursor and PointerType also live in UnityEngine, PopupWindow in UnityEditor — so this
            // patch cannot break code that already compiled.
            var uiElementsAliases = isEditorContext
                ? ""using Cursor = UnityEngine.Cursor;\nusing PointerType = UnityEngine.PointerType;\nusing PopupWindow = UnityEditor.PopupWindow;""
                : ""using Cursor = UnityEngine.Cursor;\nusing PointerType = UnityEngine.PointerType;"";

            return $@""using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.SceneManagement;
{editorUsing}
{uiElementsAliases}"));

        /// <summary>
        /// Refuse to recompile while the Editor is playing, unless the caller says otherwise.
        ///
        /// A recompile during play triggers a domain reload. The scene survives, so the objects are
        /// still there and <c>editor_status</c> still reports playing — but every static field has been
        /// reset. A singleton accessed through <c>Instance</c> reads null while
        /// <c>FindObjectsByType</c> returns the very object it should have handed back, and nothing in
        /// the response says a reload happened. The failure surfaces later, in whatever command
        /// depended on that state, which is the worst place to discover it.
        ///
        /// <c>editor_status</c>'s <c>frameCount</c> makes the reload detectable after the fact. This makes the common
        /// cause avoidable: stop, recompile, play. The escape hatch stays for callers who know what
        /// they are doing.
        /// </summary>
        public static PipelinePatch RecompilePlayModeGuard => new PipelinePatch(
            id: "recompile_playmode_guard",
            title: "recompile: refuse while playing unless forced",
            description: "Recompiling during play triggers a domain reload that resets every static field while the scene and " +
                         "the reported playMode stay unchanged — singletons read null with no error anywhere. Adds a 'force' " +
                         "parameter; without it the command returns a refusal instead.",
            new PatchEdit(
                relativePath: "Editor/Commands/RecompileCommand.cs",
                anchor:
@"        public static object Recompile(
            [CliArg(""focus"", ""If true, bring the Editor to the foreground before compiling. Off by default."")] bool focus = false)
        {",
                replacement:
@"        public static object Recompile(
            [CliArg(""focus"", ""If true, bring the Editor to the foreground before compiling. Off by default."")] bool focus = false,
            [CliArg(""force"", ""Recompile even while playing. A domain reload will reset all static state."")] bool force = false)
        {
            // [MoreUpipe:recompile_playmode_guard] A reload during play keeps the scene and the reported
            // playMode intact while clearing every static, so the damage only shows up later.
            if (EditorApplication.isPlaying && !force)
            {
                return new
                {
                    status = ""refused"",
                    reason = ""editor is playing"",
                    detail = ""Recompiling now triggers a domain reload that resets all static state while the scene keeps running. Stop play mode first, or pass force=true."",
                };
            }
"));
    }
}
