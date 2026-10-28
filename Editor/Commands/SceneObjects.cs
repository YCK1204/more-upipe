using UnityEngine;

namespace MoreUpipe
{
    /// <summary>Every loaded object of a type, inactive ones included.</summary>
    internal static class SceneObjects
    {
        /// <summary>
        /// All loaded <typeparamref name="T"/> instances, inactive included, in no particular order.
        ///
        /// The sort-mode overload is obsolete from Unity 6.x on, but it is the only one Unity 6.0 has,
        /// so the warning is silenced here once instead of at every call site.
        /// </summary>
        internal static T[] All<T>() where T : Object
        {
#pragma warning disable CS0618
            return Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#pragma warning restore CS0618
        }
    }
}
