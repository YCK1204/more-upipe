using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unity.Pipeline.Commands;
using UnityEditor;

namespace MoreUpipe
{
    /// <summary>
    /// Enter or leave play mode and return once the transition has actually happened.
    ///
    /// WHY this exists next to <c>editor_play</c> / <c>editor_stop</c>: those set
    /// <see cref="EditorApplication.isPlaying"/> and return in the same call, but Unity applies the
    /// change at the end of the frame. The immediate consequence is a pair of responses that
    /// contradict each other — <c>editor_stop</c> answers "Exited play mode", and the
    /// <c>editor_play</c> issued right after answers "Already in play mode", because the exit had not
    /// landed when the second command read the flag. The usual client-side fix is a fixed sleep long
    /// enough to cover the worst case, which is dead time on every call and still a guess.
    ///
    /// Here the wait happens next to the Editor and ends when the state is what was asked for.
    ///
    /// KNOWN LIMITATION: entering or leaving play mode triggers a domain reload unless the project
    /// disabled it, and a reload tears down the HTTP server along with this pending request. The
    /// caller sees a connection error rather than a result. That is why <c>settle</c> exists: with
    /// <c>settle=false</c> the command only asks for the transition, and the caller confirms it with a
    /// separate <c>editor_status</c> — one deterministic round-trip instead of a blind sleep.
    /// </summary>
    public static class PlayModeWaitCommand
    {
        /// <summary>
        /// Drive play mode to <paramref name="state"/> and, by default, block until it is there.
        ///
        /// A request for the state the Editor is already in returns immediately and reports
        /// <c>changed: false</c> — asking twice is not an error, and the caller usually cares about
        /// the resulting state rather than about having caused it.
        /// </summary>
        [CliCommand("wait.playmode",
            "Enter or exit play mode and wait for the transition to complete, instead of returning before it happens.",
            MainThreadRequired = false)]
        public static async Task<object> Set(
            [CliArg("state", "Target state: play | stop.", Required = true)] string state = null,
            [CliArg("settle", "Wait until the transition completes. False returns as soon as it is requested.")] bool settle = true,
            [CliArg("timeout", "Seconds to wait for the transition before giving up. Also the request timeout.")] double timeoutInSeconds = 60)
        {
            bool target;
            switch ((state ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "play":
                case "playing":
                    target = true;
                    break;
                case "stop":
                case "stopped":
                case "edit":
                    target = false;
                    break;
                default:
                    return new { error = "'state' must be 'play' or 'stop'." };
            }

            var timeout = WaitForCommand.UsableSeconds(timeoutInSeconds);

            var already = await OnMainThread(() => EditorApplication.isPlaying);
            if (already == target)
                return new { changed = false, playMode = already ? "playing" : "stopped", settled = true };

            // Entering play mode reloads the domain unless the project opted out, and the reload takes
            // this request down with the server. Reporting that up front beats holding a connection
            // that is about to be severed and handing the caller a transport error to interpret.
            var reloadWillDrop = target && await OnMainThread(DomainReloadOnEnterPlayMode);

            await OnMainThread(() =>
            {
                EditorApplication.isPlaying = target;
                return true;
            });

            if (!settle)
                return new { changed = true, requested = target ? "playing" : "stopped", settled = false };

            if (reloadWillDrop)
                return new
                {
                    changed = true,
                    requested = "playing",
                    settled = false,
                    reason = "domain reload",
                    detail = "Entering play mode reloads the domain, which drops this connection before it can answer. " +
                             "Play mode was requested; confirm with 'unity command editor status', or 'unity command wait until' a condition in the running scene. " +
                             "Disable Edit > Project Settings > Editor > Enter Play Mode Options > Reload Domain to make this wait work.",
                };

            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed.TotalSeconds < timeout)
            {
                // isPlayingOrWillChangePlaymode stays true through the whole transition, so waiting on
                // isPlaying alone reports success while the Editor is still mid-switch.
                var settledNow = await OnMainThread(() =>
                    EditorApplication.isPlaying == target &&
                    EditorApplication.isPlayingOrWillChangePlaymode == target &&
                    !EditorApplication.isCompiling &&
                    !EditorApplication.isUpdating);

                if (settledNow)
                    return new
                    {
                        changed = true,
                        playMode = target ? "playing" : "stopped",
                        settled = true,
                        elapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
                    };

                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }

            return new
            {
                changed = true,
                requested = target ? "playing" : "stopped",
                settled = false,
                reason = "timeout",
                elapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
            };
        }

        /// <summary>
        /// Whether entering play mode will reload the domain, which is what decides if a caller can be
        /// answered at all. A reload is the default; it is skipped only when the project enabled
        /// Enter Play Mode Options and ticked Reload Domain off.
        /// </summary>
        static bool DomainReloadOnEnterPlayMode()
        {
            if (!EditorSettings.enterPlayModeOptionsEnabled)
                return true;

            return (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0;
        }

        /// <summary>
        /// Run <paramref name="work"/> on the main thread. The command is declared
        /// <c>MainThreadRequired = false</c> so the polling delay leaves the Editor free to actually
        /// perform the transition being waited on.
        /// </summary>
        static Task<T> OnMainThread<T>(Func<T> work)
        {
            var completion = new TaskCompletionSource<T>();

            void Tick()
            {
                EditorApplication.update -= Tick;
                try
                {
                    completion.SetResult(work());
                }
                catch (Exception)
                {
                    completion.SetResult(default);
                }
            }

            EditorApplication.update += Tick;
            return completion.Task;
        }
    }
}
