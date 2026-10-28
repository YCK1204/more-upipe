using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.Pipeline.Commands;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreUpipe
{
    /// <summary>
    /// Print the hierarchy under one object as an indented tree.
    ///
    /// <c>ugui find</c> answers "where is this object"; this answers "what is under here". The built-in
    /// <c>get_scene_hierarchy</c> covers the whole scene, which for a UI question is mostly payload the
    /// caller did not ask for — an agent pays for every line of it. Scoping to a subtree and capping
    /// the depth keeps the answer to the part being debugged.
    ///
    /// Output is plain text rather than nested JSON on purpose: a tree is read by a human or a model,
    /// and indentation carries the structure at a fraction of the tokens that nested objects with
    /// repeated key names would cost.
    /// </summary>
    public static class UiTreeCommand
    {
        /// <summary>
        /// Dump a subtree with each object's active state.
        ///
        /// Inactive objects are included and marked, because the usual question is which branch is
        /// switched off. <c>[-]</c> means the object itself is disabled; <c>[~]</c> means it is enabled
        /// but an ancestor is not, which is the case that tends to be missed.
        /// </summary>
        [CliCommand("ugui.tree", "Print the hierarchy under an object as an indented tree, inactive objects included.")]
        public static object Dump(
            [CliArg("path", "Full hierarchy path of the root. Takes precedence over name.")] string path = null,
            [CliArg("name", "Object name to use as the root. Used when no path is given.")] string name = null,
            [CliArg("depth", "How many levels below the root to include.")] int depth = 4,
            [CliArg("labels", "Include visible text next to each entry.")] bool labels = true,
            [CliArg("max_nodes", "Stop after this many entries.")] int maxNodes = 400)
        {
            if (string.IsNullOrEmpty(path) && string.IsNullOrEmpty(name))
                return new { error = "Supply at least one of 'path' or 'name'." };

            var roots = FindRoots(path, name);
            if (roots.Count == 0)
                return new { error = "No object matched.", path, name };

            if (roots.Count > 1)
                return new
                {
                    error = "Multiple objects matched. Pass an exact 'path'.",
                    candidates = roots.Select(HierarchyPath).ToArray(),
                };

            var builder = new StringBuilder();
            var written = 0;
            Write(roots[0], 0, depth, labels, maxNodes, builder, ref written);

            return new
            {
                root = HierarchyPath(roots[0]),
                nodes = written,
                truncated = written >= maxNodes,
                tree = builder.ToString(),
            };
        }

        static void Write(Transform target, int level, int depth, bool labels, int maxNodes, StringBuilder builder, ref int written)
        {
            if (written >= maxNodes)
                return;

            written++;

            var marker = target.gameObject.activeSelf
                ? (target.gameObject.activeInHierarchy ? " " : "~")
                : "-";

            builder.Append(new string(' ', level * 2))
                   .Append('[').Append(marker).Append("] ")
                   .Append(target.name);

            if (labels)
            {
                var label = ReadLabel(target);
                if (!string.IsNullOrEmpty(label))
                {
                    var single = label.Replace("\n", " ").Replace("\r", string.Empty);
                    if (single.Length > 40)
                        single = single.Substring(0, 40) + "…";

                    builder.Append("  \"").Append(single).Append('"');
                }
            }

            builder.Append('\n');

            if (level >= depth)
                return;

            for (var i = 0; i < target.childCount; ++i)
                Write(target.GetChild(i), level + 1, depth, labels, maxNodes, builder, ref written);
        }

        static string ReadLabel(Transform target)
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

        static List<Transform> FindRoots(string path, string name)
        {
            var all = SceneObjects.All<Transform>();

            if (!string.IsNullOrEmpty(path))
                return all.Where(transform => HierarchyPath(transform) == path).ToList();

            return all.Where(transform => transform.name == name).ToList();
        }

        static string HierarchyPath(Transform target)
        {
            var result = target.name;
            for (var cursor = target.parent; cursor != null; cursor = cursor.parent)
                result = cursor.name + "/" + result;

            return result;
        }
    }
}
