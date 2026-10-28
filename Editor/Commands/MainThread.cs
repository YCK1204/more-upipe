using System;
using System.Threading.Tasks;
using UnityEditor;

namespace MoreUpipe
{
    /// <summary>
    /// Run a piece of work on the Editor's main thread from a command declared
    /// <c>MainThreadRequired = false</c>, and await its result.
    ///
    /// WHY those commands exist at all: a command that waits (for tests, a condition, a play-mode
    /// switch) must not hold the main thread, or the very thing it waits for can never happen. It
    /// waits on a worker thread and hops over here only for the Unity API calls.
    /// </summary>
    internal static class MainThread
    {
        /// <summary>Run <paramref name="work"/> on the next Editor update and return its value.</summary>
        internal static Task<T> Run<T>(Func<T> work)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Tick()
            {
                EditorApplication.update -= Tick;
                try
                {
                    completion.SetResult(work());
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            }

            EditorApplication.update += Tick;
            return completion.Task;
        }
    }
}
