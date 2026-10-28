using System.Collections.Generic;
using System.Linq;
using Unity.Pipeline.Commands;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreUpipe
{
    /// <summary>
    /// Scene object lookup by name or visible label, including inactive objects.
    ///
    /// WHY this exists rather than using eval: <see cref="GameObject.Find"/> skips inactive objects,
    /// and UI debugging is almost always the question "which object is currently switched off?".
    /// Answering it through eval means re-sending the same
    /// <see cref="Object.FindObjectsByType{T}(FindObjectsInactive, FindObjectsSortMode)"/> sweep plus a
    /// hand-rolled parent walk on every call. This command returns the full hierarchy path and both
    /// active flags so a single round-trip identifies the object.
    ///
    /// Label matching reads <see cref="UnityEngine.UI.Text"/> and TextMeshPro components on the object
    /// itself and its direct children only. Searching the whole subtree would make every ancestor
    /// canvas match its descendants' labels, which buries the actual button in noise.
    /// </summary>
    public static class UiFindCommand
    {
        /// <summary>
        /// Find objects whose name or label matches, inactive ones included.
        ///
        /// At least one of <paramref name="name"/> or <paramref name="label"/> must be supplied;
        /// supplying both requires the object to satisfy each. Matching is substring-based unless
        /// <paramref name="exact"/> is set. Results are capped by <paramref name="limit"/> and the
        /// response reports whether the cap truncated the sweep, so a silent cut never reads as
        /// "nothing else matched".
        /// </summary>
        [CliCommand("ugui.find",
            "Find scene objects by name or visible label, inactive included. Returns hierarchy path and active state.")]
        public static object Find(
            [CliArg("name", "Object name to match. Substring unless exact=true.")] string name = null,
            [CliArg("label", "Visible text (UI.Text / TextMeshPro) to match. Substring unless exact=true.")] string label = null,
            [CliArg("exact", "Require an exact match instead of a substring match.")] bool exact = false,
            [CliArg("limit", "Maximum number of results to return.")] int limit = 30,
            [CliArg("components", "Include the component type names on each result.")] bool components = false)
        {
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(label))
                return new { error = "Supply at least one of 'name' or 'label'." };

            var results = new List<object>();
            var all = SceneObjects.All<Transform>();

            foreach (var transform in all)
            {
                if (results.Count >= limit)
                    break;

                if (!string.IsNullOrEmpty(name) && !Matches(transform.name, name, exact))
                    continue;

                var foundLabel = ReadLabel(transform);
                if (!string.IsNullOrEmpty(label) && !Matches(foundLabel, label, exact))
                    continue;

                results.Add(new
                {
                    path = HierarchyPath(transform),
                    name = transform.name,
                    activeSelf = transform.gameObject.activeSelf,
                    activeInHierarchy = transform.gameObject.activeInHierarchy,
                    label = foundLabel,
                    components = components ? ComponentNames(transform) : null,
                });
            }

            return new { count = results.Count, truncated = results.Count >= limit, results };
        }

        static bool Matches(string value, string query, bool exact)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            return exact ? value == query : value.Contains(query);
        }

        /// <summary>
        /// Build the full scene path ("Root/Child/Leaf") so the caller can address the object
        /// unambiguously — names alone repeat across a UI hierarchy.
        /// </summary>
        static string HierarchyPath(Transform target)
        {
            var path = target.name;
            for (var cursor = target.parent; cursor != null; cursor = cursor.parent)
                path = cursor.name + "/" + path;

            return path;
        }

        /// <summary>
        /// Read the visible label of a button-like object: its own text component, else the first
        /// direct child that carries one. Deeper descendants are deliberately not searched.
        /// </summary>
        static string ReadLabel(Transform target)
        {
            var own = ReadTextOn(target);
            if (!string.IsNullOrEmpty(own))
                return own;

            for (var i = 0; i < target.childCount; ++i)
            {
                var child = ReadTextOn(target.GetChild(i));
                if (!string.IsNullOrEmpty(child))
                    return child;
            }

            return null;
        }

        /// <summary>
        /// TextMeshPro is resolved by type name through reflection so this command still compiles in a
        /// project that does not reference the TMP assembly.
        /// </summary>
        static string ReadTextOn(Transform target)
        {
            var text = target.GetComponent<UnityEngine.UI.Text>();
            if (text != null && !string.IsNullOrEmpty(text.text))
                return text.text;

            foreach (var component in target.GetComponents<Component>())
            {
                if (component == null)
                    continue;

                var typeName = component.GetType().Name;
                if (typeName != "TextMeshProUGUI" && typeName != "TextMeshPro")
                    continue;

                var value = component.GetType().GetProperty("text")?.GetValue(component) as string;
                if (!string.IsNullOrEmpty(value))
                    return value;
            }

            return null;
        }

        static string[] ComponentNames(Transform target)
        {
            return target.GetComponents<Component>()
                .Where(component => component != null)
                .Select(component => component.GetType().Name)
                .ToArray();
        }
    }
}
