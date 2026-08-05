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
            QuietUnknownCommand,
            StatusFrameCount,
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
@"        public static ConsoleLogResponse GetConsole(
            [CliArg(""tail"", ""Maximum number of most-recent entries to return"")] int tail = DefaultTail,
            [CliArg(""level"", ""Minimum severity to include: log | warn | error"")] string level = ConsoleLogBuffer.LevelLog,
            [CliArg(""since"", ""Cursor: only return entries newer than this seq. Use the 'cursor' from a previous response to follow."")] long since = -1)
        {
            // Guard against nonsensical tail values; <= 0 would otherwise mean ""unlimited"" in the
            // buffer, which is not what a caller passing e.g. --tail 0 expects.
            if (tail <= 0)
                tail = DefaultTail;

            var minSeverity = ConsoleLogBuffer.SeverityFromLevelName(level);
            return ConsoleLogCapture.Buffer.Query(since, tail, minSeverity);
        }",
                replacement:
@"        public static ConsoleLogResponse GetConsole(
            [CliArg(""tail"", ""Maximum number of most-recent entries to return"")] int tail = DefaultTail,
            [CliArg(""level"", ""Minimum severity to include: log | warn | error"")] string level = ConsoleLogBuffer.LevelLog,
            [CliArg(""since"", ""Cursor: only return entries newer than this seq. Use the 'cursor' from a previous response to follow."")] long since = -1,
            [CliArg(""grep"", ""Only return entries whose message or stack trace contains this text (case-insensitive)."")] string grep = null,
            [CliArg(""grep_not"", ""Drop entries whose message or stack trace contains this text (case-insensitive)."")] string grepNot = null,
            [CliArg(""stack"", ""Include stack traces. Set false to strip them from the response."")] bool stack = true)
        {
            // Guard against nonsensical tail values; <= 0 would otherwise mean ""unlimited"" in the
            // buffer, which is not what a caller passing e.g. --tail 0 expects.
            if (tail <= 0)
                tail = DefaultTail;

            var minSeverity = ConsoleLogBuffer.SeverityFromLevelName(level);
            var response = ConsoleLogCapture.Buffer.Query(since, tail, minSeverity);

            if (response?.Entries == null)
                return response;

            var hasTextFilter = !string.IsNullOrEmpty(grep) || !string.IsNullOrEmpty(grepNot);
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

            response.Entries = kept.ToArray();
            response.Returned = kept.Count;
            return response;
        }"));

        /// <summary>
        /// Stop an unknown command name from echoing the entire command list back.
        ///
        /// The original error embeds every registered command — around 2 KB of names for a single typo
        /// — and that text also lands in the Editor console, so it keeps resurfacing in later log
        /// queries. The replacement keeps the diagnosis useful by listing only names that resemble what
        /// was asked for.
        /// </summary>
        public static PipelinePatch QuietUnknownCommand => new PipelinePatch(
            id: "quiet_unknown_command",
            title: "Unknown command: suggest instead of dumping every name",
            description: "A mistyped command name currently returns the full command list (about 2 KB) and logs it to the " +
                         "console as well. This returns only similar names.",
            new PatchEdit(
                relativePath: "Runtime/Common/BasePipelineServer.cs",
                anchor:
@"                var availableCommands = string.Join("", "", commands.Select(c => c.Name));
                var errorMessage = $""No command named '{commandName}' is available. Available: [{availableCommands}]"";",
                replacement:
@"                var similar = commands
                    .Select(c => c.Name)
                    .Where(n => n.IndexOf(commandName ?? string.Empty, System.StringComparison.OrdinalIgnoreCase) >= 0
                                || (commandName != null && commandName.IndexOf(n, System.StringComparison.OrdinalIgnoreCase) >= 0))
                    .Take(5)
                    .ToList();

                var availableCommands = similar.Count > 0
                    ? string.Join("", "", similar)
                    : $""{commands.Count} commands registered; call /api/commands for the list"";
                var errorMessage = $""No command named '{commandName}' is available. Did you mean: [{availableCommands}]"";"));

        /// <summary>
        /// Expose the frame counter through <c>editor_status</c>.
        ///
        /// <c>editor_status</c> keeps reporting playMode "playing" after a domain reload has restarted
        /// the scene, so a caller polling it believes the session it set up is still there while every
        /// object in it is gone. Frame count is the cheapest signal that separates the two: it resets,
        /// so a value far below the last observed one means the world was rebuilt.
        ///
        /// The command and its response model are edited together — either alone does not compile.
        /// </summary>
        public static PipelinePatch StatusFrameCount => new PipelinePatch(
            id: "status_frame_count",
            title: "editor_status: report frame count",
            description: "Adds 'frameCount' to editor_status. playMode still reads 'playing' after a domain reload restarts " +
                         "the scene; a frame count that dropped is how you detect the session was rebuilt.",
            new PatchEdit(
                relativePath: "Editor/Commands/EditorStatusCommand.cs",
                anchor:
@"                ProjectPath = Path.GetDirectoryName(Application.dataPath),
                UnityVersion = Application.unityVersion",
                replacement:
@"                ProjectPath = Path.GetDirectoryName(Application.dataPath),
                UnityVersion = Application.unityVersion,
                FrameCount = Time.frameCount"),
            new PatchEdit(
                relativePath: "Runtime/Models/StatusResponse.cs",
                anchor:
@"        [JsonProperty(""unityVersion"")]
        public string UnityVersion { get; set; }",
                replacement:
@"        [JsonProperty(""unityVersion"")]
        public string UnityVersion { get; set; }

        /// <summary>
        /// Frames rendered since startup. Resets when a domain reload restarts the scene, which
        /// playMode alone does not reveal.
        /// </summary>
        [JsonProperty(""frameCount"")]
        public int FrameCount { get; set; }"));
    }
}
