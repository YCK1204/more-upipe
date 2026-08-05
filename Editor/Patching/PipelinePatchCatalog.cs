using System.Collections.Generic;

namespace MoreUpipe.Patching
{
    /// <summary>
    /// The patch set. Every entry names one file in <c>com.unity.pipeline</c>, the exact snippet it
    /// attaches to, and the text that replaces it.
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
        };

        /// <summary>
        /// Server-side text filtering for the <c>console</c> command.
        ///
        /// Without it the whole buffer crosses the wire and the caller greps it locally — which for an
        /// agent means the entire payload lands in its context first. A 40-entry response measured
        /// 58 KB in testing; the same query filtered to the lines of interest was 273 bytes. The filter
        /// runs on message and stack trace, the two fields worth searching.
        /// </summary>
        public static PipelinePatch ConsoleFilter => new PipelinePatch(
            id: "console_filter",
            title: "console: server-side text filter",
            description: "Adds 'grep' and 'grep_not' parameters to the console command so matching happens in the Editor " +
                         "instead of shipping the whole buffer to the caller. A 40-entry response measured 58 KB unfiltered, " +
                         "273 bytes filtered.",
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
            [CliArg(""grep_not"", ""Drop entries whose message or stack trace contains this text (case-insensitive)."")] string grepNot = null)
        {
            // Guard against nonsensical tail values; <= 0 would otherwise mean ""unlimited"" in the
            // buffer, which is not what a caller passing e.g. --tail 0 expects.
            if (tail <= 0)
                tail = DefaultTail;

            var minSeverity = ConsoleLogBuffer.SeverityFromLevelName(level);
            var response = ConsoleLogCapture.Buffer.Query(since, tail, minSeverity);

            if (response?.Entries == null || (string.IsNullOrEmpty(grep) && string.IsNullOrEmpty(grepNot)))
                return response;

            var kept = new System.Collections.Generic.List<ConsoleLogEntry>();
            foreach (var entry in response.Entries)
            {
                var haystack = (entry?.Message ?? string.Empty) + ""\n"" + (entry?.StackTrace ?? string.Empty);

                if (!string.IsNullOrEmpty(grep) &&
                    haystack.IndexOf(grep, System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (!string.IsNullOrEmpty(grepNot) &&
                    haystack.IndexOf(grepNot, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                kept.Add(entry);
            }

            response.Entries = kept.ToArray();
            response.Returned = kept.Count;
            return response;
        }");

        /// <summary>
        /// Stop an unknown command name from echoing the entire command list back.
        ///
        /// The original error embeds every registered command — around 2 KB of names for a typo — and
        /// that text also lands in the Editor console, so it keeps resurfacing in later log queries.
        /// The replacement keeps the diagnosis useful by listing only names that resemble what was
        /// asked for.
        /// </summary>
        public static PipelinePatch QuietUnknownCommand => new PipelinePatch(
            id: "quiet_unknown_command",
            title: "Unknown command: suggest instead of dumping every name",
            description: "A mistyped command name currently returns the full ~140-entry command list (about 2 KB) and logs it " +
                         "to the console as well. This returns only similar names.",
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
                var errorMessage = $""No command named '{commandName}' is available. Did you mean: [{availableCommands}]"";");
    }
}
