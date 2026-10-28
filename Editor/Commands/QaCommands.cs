using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;

namespace MoreUpipe
{
    /// <summary>
    /// The <c>qa</c> group: the checks an agent runs after every change, shaped to cost as few calls and
    /// as few tokens as possible.
    ///
    /// WHY a separate group: the stock commands answer one question each and answer it in full. After
    /// an edit the questions are always the same — did it compile, did anything throw, do the tests
    /// pass — and each stock answer costs a round trip of about two seconds plus a payload sized for a
    /// human scrolling a window. These commands answer them in one call each, collapse repeats, and
    /// leave out everything that is not a problem.
    /// </summary>
    public static class QaCommands
    {
        const string Group = "qa";

        /// <summary>
        /// One-call health check: Editor state, compile errors, console totals and the newest distinct
        /// errors.
        /// </summary>
        [CliCommand(Group, "check",
            "One call after a change: Editor state, compile errors, console totals and the newest distinct errors, repeats collapsed.")]
        public static object Check(
            [CliArg("top", "Distinct runtime errors to include, newest first.")] int top = 5,
            [CliArg("chars", "Maximum characters per message.")] int chars = 200)
        {
            var problem = EditorConsole.Unavailable;
            var counts = EditorConsole.Counts();

            var compileErrors = new List<string>();
            var errors = new List<Dictionary<string, object>>();
            var byMessage = new Dictionary<string, Dictionary<string, object>>();

            if (problem == null)
            {
                EditorConsole.ReadNewestFirst(0, entry =>
                {
                    if (entry.Level != EditorConsole.LevelError)
                        return true;

                    if (entry.Compile)
                    {
                        if (compileErrors.Count < 20)
                            compileErrors.Add(Truncate(entry.Message, chars));
                        return true;
                    }

                    var key = entry.Message;
                    if (byMessage.TryGetValue(key, out var seen))
                    {
                        seen["count"] = (int)seen["count"] + 1;
                        return true;
                    }

                    if (byMessage.Count >= top)
                        return true;

                    var item = Shape(entry, 1, chars, 0);
                    byMessage[key] = item;
                    errors.Add(item);
                    return true;
                });
            }

            var result = new Dictionary<string, object>
            {
                ["ready"] = !EditorApplication.isCompiling && !EditorApplication.isUpdating,
                ["playMode"] = EditorApplication.isPlaying ? "playing" : "stopped",
                ["compileFailed"] = EditorUtility.scriptCompilationFailed,
                ["console"] = new Dictionary<string, int> { ["errors"] = counts.errors, ["warnings"] = counts.warnings, ["logs"] = counts.logs },
            };

            if (EditorApplication.isCompiling)
                result["compiling"] = true;
            if (QaTestRuns.IsRunning)
                result["testsRunning"] = true;
            if (compileErrors.Count > 0)
                result["compileErrors"] = compileErrors;
            if (errors.Count > 0)
                result["errors"] = errors;
            if (problem != null)
                result["note"] = problem;

            return result;
        }

        /// <summary>
        /// Editor console with server-side filtering and repeats collapsed into one line with a count.
        /// </summary>
        [CliCommand(Group, "log",
            "Editor console, filtered on the server: level, grep / grep_not ('|' separates alternatives), repeats collapsed with a count, stack traces off by default.")]
        public static object Log(
            [CliArg("level", "Minimum severity: log | warn | error.")] string level = "error",
            [CliArg("grep", "Keep entries whose message contains any of these ('|'-separated, case-insensitive).")] string grep = null,
            [CliArg("grep_not", "Drop entries whose message contains any of these ('|'-separated, case-insensitive).")] string grepNot = null,
            [CliArg("tail", "Maximum lines to return, newest first.")] int tail = 20,
            [CliArg("collapse", "Merge identical messages into one line with a count.")] bool collapse = true,
            [CliArg("stack", "Stack trace lines to include per entry. 0 leaves them out.")] int stack = 0,
            [CliArg("chars", "Maximum characters per message.")] int chars = 300,
            [CliArg("since", "Only entries after this cursor (the 'cursor' of an earlier answer). -1 reads everything.")] int since = -1,
            [CliArg("scan", "Newest entries to examine. 0 examines the whole console.")] int scan = 0)
        {
            var problem = EditorConsole.Unavailable;
            if (problem != null)
                return new Dictionary<string, object> { ["error"] = problem };

            var minimum = EditorConsole.Severity(EditorConsole.ParseLevel(level));
            var include = SplitTerms(grep);
            var exclude = SplitTerms(grepNot);
            tail = Math.Max(1, tail);

            var lines = new List<Dictionary<string, object>>();
            var byMessage = new Dictionary<string, Dictionary<string, object>>();
            var matched = 0;
            var scanned = 0;

            var total = EditorConsole.ReadNewestFirst(scan, entry =>
            {
                if (entry.Index <= since)
                    return false;

                scanned++;
                if (EditorConsole.Severity(entry.Level) < minimum)
                    return true;
                if (include.Length > 0 && !ContainsAny(entry.Message, include))
                    return true;
                if (exclude.Length > 0 && ContainsAny(entry.Message, exclude))
                    return true;

                matched++;
                if (collapse && byMessage.TryGetValue(entry.Message, out var seen))
                {
                    seen["count"] = (int)seen["count"] + 1;
                    return true;
                }

                // Keep scanning once the window is full only when collapsing, so the counts stay right.
                if (lines.Count >= tail)
                    return collapse;

                var item = Shape(entry, 1, chars, stack);
                lines.Add(item);
                if (collapse)
                    byMessage[entry.Message] = item;
                return true;
            });

            if (!collapse)
                foreach (var item in lines)
                    item.Remove("count");

            var counts = EditorConsole.Counts();
            return new Dictionary<string, object>
            {
                ["cursor"] = total - 1,
                ["console"] = new Dictionary<string, int> { ["errors"] = counts.errors, ["warnings"] = counts.warnings, ["logs"] = counts.logs },
                ["scanned"] = scanned,
                ["matched"] = matched,
                ["lines"] = lines,
            };
        }

        /// <summary>
        /// Run tests and answer with counts plus the failures only. Fails the CLI call (non-zero exit)
        /// when any test fails or nothing matched, unless <c>strict=false</c>.
        /// </summary>
        [CliCommand(Group, "tests",
            "Run tests, answer with counts and failures only. Exits non-zero on failure or when nothing matched (strict). Pass --timeout for long suites.",
            MainThreadRequired = false)]
        public static async Task<object> Tests(
            [CliArg("filter", "Substring of the test full name ('|' separates alternatives). Empty runs everything in the mode.")] string filter = null,
            [CliArg("mode", "editor | play.")] string mode = "editor",
            [CliArg("category", "Only tests in this NUnit category.")] string category = null,
            [CliArg("failed", "Run only the tests known to be failing: failed in some run and not passed since.")] bool failed = false,
            [CliArg("strict", "Exit non-zero when a test fails or nothing matched.")] bool strict = true,
            [CliArg("max_failures", "Failures to list in full.")] int maxFailures = QaTestRuns.DefaultMaxFailures,
            [CliArg("chars", "Maximum characters per failure message.")] int chars = QaTestRuns.DefaultMessageChars,
            [CliArg("timeout", "Seconds to wait for the run. Also the request timeout.")] double timeoutInSeconds = 600)
        {
            var testMode = (mode ?? string.Empty).Trim().ToLowerInvariant().StartsWith("play")
                ? TestMode.PlayMode
                : TestMode.EditMode;

            var started = await MainThread.Run<(int run, Task<QaRunSummary> finished, string label, string error)>(() =>
            {
                if (QaTestRuns.IsRunning)
                    return (0, null, null,
                        "A test run is already in progress. Wait for it, or read it later with 'qa test_result'.");

                var testFilter = new Filter { testMode = testMode };
                string label;

                if (failed)
                {
                    var names = QaTestRuns.FailingNames;
                    if (names.Length == 0)
                        return (0, null, null, "No test is known to be failing in this session.");
                    testFilter.testNames = names;
                    label = $"(failing {names.Length})";
                }
                else
                {
                    var terms = SplitTerms(filter);
                    if (terms.Length > 0)
                        testFilter.groupNames = terms.Select(Regex.Escape).ToArray();
                    label = terms.Length > 0 ? string.Join("|", terms) : "(all)";
                }

                if (!string.IsNullOrWhiteSpace(category))
                {
                    testFilter.categoryNames = new[] { category.Trim() };
                    label += $" category={category.Trim()}";
                }

                var (run, finished) = QaTestRuns.Start(testFilter, label, Math.Max(0, maxFailures), Math.Max(40, chars));
                return (run, finished, label, (string)null);
            });

            if (started.error != null)
                return Fail(strict, started.error);

            var budget = WaitForCommand.UsableSeconds(timeoutInSeconds);
            var winner = double.IsPositiveInfinity(budget)
                ? started.finished
                : await Task.WhenAny(started.finished, Task.Delay(TimeSpan.FromSeconds(budget)));

            if (winner != started.finished)
                return Fail(strict,
                    $"Run {started.run} ({started.label}) is still going after {Math.Round(budget)}s. " +
                    "It keeps running; read the result with 'unity command qa test_result', or pass a larger --timeout.");

            var summary = started.finished.Result;

            if (summary.total == 0)
                return Fail(strict, $"Run {summary.run}: no {summary.mode} tests matched {summary.filter}.");

            if (strict && (summary.failed > 0 || summary.inconclusive > 0))
                throw new TestsFailedException(Describe(summary));

            return Shape(summary, false);
        }

        /// <summary>
        /// A run summary without the fields that carry nothing: zero counts, an empty failure list.
        /// A passing run answers in a single short object.
        /// </summary>
        static Dictionary<string, object> Shape(QaRunSummary summary, bool withTime)
        {
            var result = new Dictionary<string, object>
            {
                ["run"] = summary.run,
                ["mode"] = summary.mode,
                ["filter"] = summary.filter,
                ["total"] = summary.total,
                ["passed"] = summary.passed,
            };

            if (summary.failed > 0)
                result["failed"] = summary.failed;
            if (summary.skipped > 0)
                result["skipped"] = summary.skipped;
            if (summary.inconclusive > 0)
                result["inconclusive"] = summary.inconclusive;
            result["seconds"] = summary.seconds;
            if (withTime)
                result["finishedAt"] = summary.finishedAt;
            if (summary.failures.Count > 0)
                result["failures"] = summary.failures;
            if (summary.failuresTruncated)
                result["failuresTruncated"] = true;

            return result;
        }

        /// <summary>
        /// The last finished run's summary — the way back to a result after a dropped connection.
        /// </summary>
        [CliCommand(Group, "test_result",
            "Summary of the last finished test run, with its run number. Use it after a dropped connection.")]
        public static object TestResult()
        {
            var last = QaTestRuns.Last;
            if (last == null)
                return new Dictionary<string, object> { ["note"] = "No test run has finished in this Editor session." };

            if (QaTestRuns.IsRunning)
                return new Dictionary<string, object> { ["running"] = true, ["last"] = Shape(last, true) };

            return Shape(last, true);
        }

        /// <summary>
        /// Test inventory by fixture: names and counts only, never the per-test records.
        /// </summary>
        [CliCommand(Group, "test_list",
            "Test inventory grouped by fixture with counts. Pass names=true to list test names (capped by limit).",
            MainThreadRequired = false)]
        public static async Task<object> TestList(
            [CliArg("filter", "Substring of the test full name ('|' separates alternatives).")] string filter = null,
            [CliArg("mode", "editor | play.")] string mode = "editor",
            [CliArg("names", "List test names instead of fixtures.")] bool names = false,
            [CliArg("limit", "Maximum fixtures or names to return.")] int limit = 100,
            [CliArg("timeout", "Seconds to wait for the inventory. Also the request timeout.")] double timeoutInSeconds = 60)
        {
            var testMode = (mode ?? string.Empty).Trim().ToLowerInvariant().StartsWith("play")
                ? TestMode.PlayMode
                : TestMode.EditMode;
            var terms = SplitTerms(filter);

            var completion = new TaskCompletionSource<ITestAdaptor>(TaskCreationOptions.RunContinuationsAsynchronously);
            await MainThread.Run(() =>
            {
                var api = UnityEngine.ScriptableObject.CreateInstance<TestRunnerApi>();
                api.RetrieveTestList(testMode, root =>
                {
                    UnityEngine.Object.DestroyImmediate(api);
                    completion.TrySetResult(root);
                });
                return true;
            });

            var budget = WaitForCommand.UsableSeconds(timeoutInSeconds);
            if (await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(budget))) != completion.Task)
                return new Dictionary<string, object> { ["error"] = "The test list did not arrive in time." };

            var tests = new List<string>();
            Leaves(completion.Task.Result, tests);
            if (terms.Length > 0)
                tests = tests.Where(t => ContainsAny(t, terms)).ToList();

            limit = Math.Max(1, limit);
            if (names)
                return new Dictionary<string, object>
                {
                    ["total"] = tests.Count,
                    ["truncated"] = tests.Count > limit,
                    ["names"] = tests.Take(limit).ToList(),
                };

            var fixtures = tests
                .GroupBy(FixtureOf)
                .Select(g => $"{g.Key} ({g.Count()})")
                .ToList();

            return new Dictionary<string, object>
            {
                ["total"] = tests.Count,
                ["fixtures"] = fixtures.Count,
                ["truncated"] = fixtures.Count > limit,
                ["list"] = fixtures.Take(limit).ToList(),
            };
        }

        /// <summary>Thrown to fail the CLI call with a compact, readable reason.</summary>
        sealed class TestsFailedException : Exception
        {
            public TestsFailedException(string message) : base(message) { }
        }

        static object Fail(bool strict, string message)
        {
            if (strict)
                throw new TestsFailedException(message);
            return new Dictionary<string, object> { ["error"] = message };
        }

        static string Describe(QaRunSummary summary)
        {
            var builder = new StringBuilder();
            builder.Append($"Run {summary.run} ({summary.mode}, {summary.filter}): {summary.failed} failed");
            if (summary.inconclusive > 0)
                builder.Append($", {summary.inconclusive} inconclusive");
            builder.Append($" of {summary.total} in {summary.seconds}s.");

            foreach (var failure in summary.failures)
            {
                builder.Append("\n- ").Append(failure.name);
                if (!string.IsNullOrEmpty(failure.message))
                    builder.Append(": ").Append(failure.message);
                if (!string.IsNullOrEmpty(failure.at))
                    builder.Append(" [").Append(failure.at).Append(']');
            }

            if (summary.failuresTruncated)
                builder.Append("\n(more failures not listed; raise --max_failures)");

            return builder.ToString();
        }

        static void Leaves(ITestAdaptor node, List<string> into)
        {
            if (node == null)
                return;

            if (!node.HasChildren)
            {
                if (!node.IsSuite)
                    into.Add(node.FullName);
                return;
            }

            foreach (var child in node.Children)
                Leaves(child, into);
        }

        static string FixtureOf(string fullName)
        {
            // Parameterised cases carry their arguments in parentheses, which may contain dots.
            var paren = fullName.IndexOf('(');
            var head = paren >= 0 ? fullName.Substring(0, paren) : fullName;
            var dot = head.LastIndexOf('.');
            return dot > 0 ? head.Substring(0, dot) : head;
        }

        static Dictionary<string, object> Shape(EditorConsole.Entry entry, int count, int chars, int stackLines)
        {
            var item = new Dictionary<string, object>
            {
                ["level"] = entry.Level,
                ["count"] = count,
                ["message"] = Truncate(entry.Message, chars),
            };

            if (!string.IsNullOrEmpty(entry.File) && entry.Line > 0)
                item["at"] = entry.File.Replace('\\', '/') + ":" + entry.Line;

            if (stackLines > 0 && !string.IsNullOrEmpty(entry.StackTrace))
                item["stack"] = string.Join("\n", entry.StackTrace.Split('\n').Take(stackLines).Select(l => l.Trim()));

            return item;
        }

        static string Truncate(string text, int chars)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            chars = Math.Max(40, chars);
            var oneLine = text.Replace("\r", string.Empty).Replace('\n', ' ');
            return oneLine.Length > chars ? oneLine.Substring(0, chars) + "…" : oneLine;
        }

        static string[] SplitTerms(string value) =>
            string.IsNullOrWhiteSpace(value)
                ? Array.Empty<string>()
                : value.Split('|').Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();

        static bool ContainsAny(string text, string[] terms)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            foreach (var term in terms)
                if (text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }
    }
}
