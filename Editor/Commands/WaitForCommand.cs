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
        [CliCommand("wait_for", "Poll a C# expression until it matches, then return. Replaces client-side polling loops.",
            MainThreadRequired = false)]
        public static async Task<object> Wait(
            [CliArg("code", "C# expression body to evaluate each attempt. Must return a value.", Required = true)] string code = null,
            [CliArg("equals", "Succeed when the result equals this string.")] string equals = null,
            [CliArg("contains", "Succeed when the result contains this substring.")] string contains = null,
            [CliArg("timeout", "Seconds to keep polling before giving up.")] float timeout = 60f,
            [CliArg("interval", "Seconds between attempts.")] float interval = 1f)
        {
            if (string.IsNullOrEmpty(code))
                return new { error = "'code' is required." };

            // Guard the knobs: a zero interval turns the wait into a busy loop that starves the very
            // Editor tick the condition depends on.
            interval = Math.Max(0.1f, interval);
            timeout = Math.Max(0.1f, timeout);

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

                await Task.Delay(TimeSpan.FromSeconds(interval));
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
                    var response = Unity.Pipeline.Runtime.Commands.CodeEvalCommand.EvaluateCode(code);
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
    }
}
