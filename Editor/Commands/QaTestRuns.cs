using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace MoreUpipe
{
    /// <summary>Compact record of one finished test run, as <c>qa tests</c> reports it.</summary>
    [Serializable]
    public sealed class QaRunSummary
    {
        public int run;
        public string mode;
        public string filter;
        public int total;
        public int passed;
        public int failed;
        public int skipped;
        public int inconclusive;
        public double seconds;
        public string finishedAt;
        public bool failuresTruncated;
        public List<QaFailure> failures = new List<QaFailure>();
    }

    /// <summary>One failed or inconclusive test: its name, the first lines of its message and where it failed.</summary>
    [Serializable]
    public sealed class QaFailure
    {
        public string name;
        public string message;
        public string at;
    }

    /// <summary>
    /// Records every test run the Editor finishes, numbered, so a caller can tell its own result from
    /// someone else's.
    ///
    /// WHY a recorder that lives for the whole session: a test that enters Play Mode reloads the
    /// domain, which tears down the request that started the run along with anything it registered.
    /// Callbacks registered from <see cref="InitializeOnLoadAttribute"/> are registered again after the
    /// reload, so the run's end is still seen, and the summary goes to <see cref="SessionState"/>,
    /// which survives it. <c>qa test_result</c> reads it back, with the run number, after a dropped
    /// connection.
    /// </summary>
    [InitializeOnLoad]
    internal static class QaTestRuns
    {
        const string KeyLast = "MoreUpipe.Qa.LastRun";
        const string KeyFailing = "MoreUpipe.Qa.Failing";
        const string KeyNextRun = "MoreUpipe.Qa.NextRun";
        const string KeyPending = "MoreUpipe.Qa.Pending";
        const string KeyCurrent = "MoreUpipe.Qa.Current";

        internal const int DefaultMaxFailures = 20;
        internal const int DefaultMessageChars = 240;

        static readonly Regex s_Location = new Regex(@"(Assets[\\/][^:()\[\]]+?\.cs):(?:line )?(\d+)", RegexOptions.Compiled);

        static TestRunnerApi s_Api;
        static TaskCompletionSource<QaRunSummary> s_Waiter;
        static int s_WaiterRun;

        static QaTestRuns()
        {
            s_Api = ScriptableObject.CreateInstance<TestRunnerApi>();
            s_Api.hideFlags = HideFlags.HideAndDontSave;
            s_Api.RegisterCallbacks(new Recorder());
        }

        /// <summary>True while a run started from anywhere (CLI, Test Runner window) has not finished.</summary>
        internal static bool IsRunning => SessionState.GetInt(KeyCurrent, 0) != 0;

        /// <summary>The last finished run, or null when none finished this session.</summary>
        internal static QaRunSummary Last
        {
            get
            {
                var json = SessionState.GetString(KeyLast, null);
                return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<QaRunSummary>(json);
            }
        }

        /// <summary>
        /// Full names of the tests currently known to fail: added when a run sees them fail, dropped
        /// when a later run sees them pass.
        /// </summary>
        internal static string[] FailingNames
        {
            get
            {
                var joined = SessionState.GetString(KeyFailing, string.Empty);
                return joined.Length == 0 ? Array.Empty<string>() : joined.Split('\n');
            }
        }

        /// <summary>
        /// Start a run and hand back its number plus a task that completes with that run's summary.
        /// Main thread only.
        /// </summary>
        internal static (int run, Task<QaRunSummary> finished) Start(Filter filter, string filterLabel,
            int maxFailures, int messageChars)
        {
            var run = SessionState.GetInt(KeyNextRun, 1);
            SessionState.SetInt(KeyNextRun, run + 1);
            SessionState.SetInt(KeyPending, run);
            SessionState.SetString(KeyPending + ".Filter", filterLabel ?? string.Empty);
            SessionState.SetInt(KeyPending + ".Max", maxFailures);
            SessionState.SetInt(KeyPending + ".Chars", messageChars);

            s_Waiter = new TaskCompletionSource<QaRunSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
            s_WaiterRun = run;

            s_Api.Execute(new ExecutionSettings(filter));
            return (run, s_Waiter.Task);
        }

        sealed class Recorder : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
                // A run started by qa tests claims its number; one started elsewhere gets the next one.
                var run = SessionState.GetInt(KeyPending, 0);
                if (run == 0)
                {
                    run = SessionState.GetInt(KeyNextRun, 1);
                    SessionState.SetInt(KeyNextRun, run + 1);
                    SessionState.SetString(KeyPending + ".Filter", "(started outside qa tests)");
                    SessionState.SetInt(KeyPending + ".Max", DefaultMaxFailures);
                    SessionState.SetInt(KeyPending + ".Chars", DefaultMessageChars);
                }

                SessionState.SetInt(KeyPending, 0);
                SessionState.SetInt(KeyCurrent, run);
                SessionState.SetString(KeyCurrent + ".Mode", testsToRun?.TestMode.ToString() ?? string.Empty);
                SessionState.SetString(KeyCurrent + ".Filter", SessionState.GetString(KeyPending + ".Filter", string.Empty));
                SessionState.SetInt(KeyCurrent + ".Max", SessionState.GetInt(KeyPending + ".Max", DefaultMaxFailures));
                SessionState.SetInt(KeyCurrent + ".Chars", SessionState.GetInt(KeyPending + ".Chars", DefaultMessageChars));
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                var run = SessionState.GetInt(KeyCurrent, 0);
                var summary = Summarize(result,
                    SessionState.GetInt(KeyCurrent + ".Max", DefaultMaxFailures),
                    SessionState.GetInt(KeyCurrent + ".Chars", DefaultMessageChars));
                summary.run = run;
                summary.mode = SessionState.GetString(KeyCurrent + ".Mode", string.Empty);
                summary.filter = SessionState.GetString(KeyCurrent + ".Filter", string.Empty);

                SessionState.SetString(KeyLast, JsonUtility.ToJson(summary));
                SessionState.SetInt(KeyCurrent, 0);

                if (s_Waiter != null && s_WaiterRun == run)
                {
                    var waiter = s_Waiter;
                    s_Waiter = null;
                    waiter.TrySetResult(summary);
                }
            }

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result) { }
        }

        static QaRunSummary Summarize(ITestResultAdaptor root, int maxFailures, int messageChars)
        {
            var summary = new QaRunSummary
            {
                passed = root.PassCount,
                failed = root.FailCount,
                skipped = root.SkipCount,
                inconclusive = root.InconclusiveCount,
                seconds = Math.Round(root.Duration, 2),
                finishedAt = DateTime.Now.ToString("HH:mm:ss"),
            };
            summary.total = summary.passed + summary.failed + summary.skipped + summary.inconclusive;

            // The failing set is carried across runs: a run adds what failed and drops what passed, and
            // leaves every test it did not run alone. A passing run of some unrelated fixture therefore
            // does not forget the failures a full run found earlier.
            var failing = new HashSet<string>(FailingNames);
            CollectFailures(root, summary, failing, maxFailures, messageChars);
            SessionState.SetString(KeyFailing, string.Join("\n", failing));
            return summary;
        }

        static void CollectFailures(ITestResultAdaptor node, QaRunSummary summary, HashSet<string> failing,
            int maxFailures, int messageChars)
        {
            if (node.HasChildren)
            {
                foreach (var child in node.Children)
                    CollectFailures(child, summary, failing, maxFailures, messageChars);
                return;
            }

            if (node.TestStatus == TestStatus.Passed)
                failing.Remove(node.FullName);

            if (node.TestStatus != TestStatus.Failed && node.TestStatus != TestStatus.Inconclusive)
                return;

            failing.Add(node.FullName);
            if (summary.failures.Count >= maxFailures)
            {
                summary.failuresTruncated = true;
                return;
            }

            summary.failures.Add(new QaFailure
            {
                name = node.FullName,
                message = Compact(node.Message, messageChars),
                at = Locate(node.StackTrace),
            });
        }

        /// <summary>Collapse a multi-line assertion message into one line and cap its length.</summary>
        internal static string Compact(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            var builder = new StringBuilder();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                    continue;
                if (builder.Length > 0)
                    builder.Append(" / ");
                builder.Append(line);
                if (builder.Length >= maxChars)
                    break;
            }

            var compact = builder.ToString();
            return compact.Length > maxChars ? compact.Substring(0, maxChars) + "…" : compact;
        }

        /// <summary>The first project source location in a stack trace, as <c>Assets/…/File.cs:123</c>.</summary>
        internal static string Locate(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace))
                return null;

            var match = s_Location.Match(stackTrace);
            return match.Success ? match.Groups[1].Value.Replace('\\', '/') + ":" + match.Groups[2].Value : null;
        }
    }
}
