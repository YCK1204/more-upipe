using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unity.Pipeline.Commands;
using UnityEditor;

namespace MoreUpipe
{
    /// <summary>
    /// Block until a scripted condition holds, then return.
    ///
    /// WHY this belongs on the server: without it the caller polls — issue a command, inspect the
    /// answer, sleep, repeat — and every one of those round-trips is a full request/response pair the
    /// agent has to read. Waiting for a login to reach the lobby cost twenty-plus calls whose only
    /// content was "not yet". Here the loop runs next to the Editor and one response reports the
    /// outcome, the elapsed time, and the value that ended the wait.
    ///
    /// The condition is evaluated through <see cref="EditorApplication.ExecuteMenuItem"/>'s sibling
    /// facility — the pipeline's own eval command — so anything expressible there works, including
    /// project types. Evaluation happens on the main thread; the delay between attempts yields so the
    /// Editor keeps ticking and the state under test can actually change.
    /// </summary>
    public static class WaitForCommand
    {
        /// <summary>
        /// Poll <paramref name="code"/> until its result satisfies the comparison, or the timeout
        /// elapses.
        ///
        /// Exactly one of <paramref name="equals"/> or <paramref name="contains"/> is expected; with
        /// neither, any non-empty result that is not "false" or "null" counts as success, which covers
        /// the common "wait until this stops being null" case.
        ///
        /// A timeout is reported as <c>succeeded: false</c> with the last observed value rather than
        /// raised as an error — a wait that ran out is a result, not a malfunction, and the last value
        /// is usually what explains why.
        /// </summary>
        [CliCommand("wait.until", "Poll a C# expression until it matches, then return. Replaces client-side polling loops.",
            MainThreadRequired = false)]
        public static async Task<object> Wait(
            [CliArg("code", "C# expression body to evaluate each attempt. Must return a value.", Required = true)] string code = null,
            [CliArg("equals", "Succeed when the result equals this string.")] string equals = null,
            [CliArg("contains", "Succeed when the result contains this substring.")] string contains = null,
            [CliArg("timeout", "Seconds to keep polling before giving up. Also the request timeout.")] double timeoutInSeconds = 60,
            [CliArg("interval", "Seconds between attempts.")] float interval = 1f)
        {
            if (string.IsNullOrEmpty(code))
                return new { error = "'code' is required." };

            // Guard the knobs: a zero interval turns the wait into a busy loop that starves the very
            // Editor tick the condition depends on.
            interval = Math.Max(0.1f, interval);
            var timeout = UsableSeconds(timeoutInSeconds);

            var stopwatch = Stopwatch.StartNew();
            string last = null;
            var attempts = 0;

            while (stopwatch.Elapsed.TotalSeconds < timeout)
            {
                attempts++;
                last = await EvaluateOnMainThread(code);

                if (Satisfied(last, equals, contains))
                    return new
                    {
                        succeeded = true,
                        value = last,
                        attempts,
                        elapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
                    };

                var remaining = timeout - stopwatch.Elapsed.TotalSeconds;
                if (remaining <= 0)
                    break;

                await Task.Delay(TimeSpan.FromSeconds(Math.Min(interval, remaining)));
            }

            return new
            {
                succeeded = false,
                reason = "timeout",
                value = last,
                attempts,
                elapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
            };
        }

        static bool Satisfied(string value, string equals, string contains)
        {
            if (value == null)
                return false;

            if (!string.IsNullOrEmpty(equals))
                return value == equals;

            if (!string.IsNullOrEmpty(contains))
                return value.Contains(contains);

            // No comparison supplied: treat "has a real value" as the condition.
            return value.Length > 0
                   && !value.Equals("null", StringComparison.OrdinalIgnoreCase)
                   && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Run the expression on the main thread and hand back its result as a string. Unity API access
        /// is illegal off-thread, and this command is declared <c>MainThreadRequired = false</c> so the
        /// polling delay does not block the Editor between attempts.
        /// </summary>
        static Task<string> EvaluateOnMainThread(string code)
        {
            var completion = new TaskCompletionSource<string>();

            void Tick()
            {
                EditorApplication.update -= Tick;
                try
                {
                    var eval = FindScriptEval();
                    if (eval == null)
                    {
                        completion.SetResult("error: the pipeline 'script eval' command is not registered");
                        return;
                    }

                    var response = eval.Method.Invoke(eval.Target, new object[] { code, 30d })
                        as Unity.Pipeline.Models.CommandExecutionResponse;
                    completion.SetResult(response == null || !response.Success
                        ? null
                        : response.Result?.ToString());
                }
                catch (Exception exception)
                {
                    completion.SetResult("error: " + exception.Message);
                }
            }

            EditorApplication.update += Tick;
            return completion.Task;
        }

        /// <summary>
        /// How long a waiting command may poll, given the <c>timeout</c> the server hands it.
        ///
        /// WHY stop early: since com.unity.pipeline 0.9 a parameter named <c>timeout</c> is the request
        /// timeout, and the server writes the time left into it. When it runs out the server answers
        /// 504 itself, so a handler that waits the full amount never gets to report its own
        /// <c>reason = "timeout"</c>. Keep a margin of a tenth of the budget, at most one second.
        /// </summary>
        internal static double UsableSeconds(double timeoutInSeconds)
        {
            if (double.IsPositiveInfinity(timeoutInSeconds))
                return double.PositiveInfinity;

            var margin = Math.Min(1.0, timeoutInSeconds * 0.1);
            return Math.Max(0.1, timeoutInSeconds - margin);
        }

        static CommandInfo s_ScriptEval;

        /// <summary>
        /// The pipeline's <c>script eval</c> command, looked up through the public registry.
        ///
        /// WHY not call the eval class directly: since com.unity.pipeline 0.8 that class is internal.
        /// The registry entry is public and survives the class being renamed or moved.
        /// </summary>
        static CommandInfo FindScriptEval()
        {
            if (s_ScriptEval != null)
                return s_ScriptEval;

            foreach (var command in CommandRegistry.DiscoverCommands())
            {
                if (command.Group == "script" && command.Name == "eval")
                {
                    s_ScriptEval = command;
                    break;
                }
            }

            return s_ScriptEval;
        }
    }
}
