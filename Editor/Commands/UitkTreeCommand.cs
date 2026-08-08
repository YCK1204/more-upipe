using System.Collections.Generic;
using System.Text;
using Unity.Pipeline.Commands;
using UnityEngine.UIElements;

namespace MoreUpipe
{
    /// <summary>
    /// Print the UI Toolkit element tree under a panel or a named element.
    ///
    /// This is <c>ui_tree</c>'s counterpart for UI Toolkit. It walks
    /// <see cref="VisualElement.hierarchy"/> rather than <c>Children()</c>, so the generated parts of a
    /// composite control are visible — the checkmark a Toggle attaches, the scroller a ScrollView owns.
    /// Those parts are usually the answer when a widget renders wrong, and the content-container view
    /// hides them.
    ///
    /// Output is indented text rather than nested JSON for the same reason <c>ui_tree</c> gives:
    /// indentation carries the structure at a fraction of what repeated JSON keys cost, and a tree is
    /// read rather than parsed.
    /// </summary>
    public static class UitkTreeCommand
    {
        /// <summary>
        /// Dump the subtree under <paramref name="name"/>, or the whole panel when no name is given.
        ///
        /// Markers mirror <c>ui_tree</c>: <c>[-]</c> the element itself is hidden, <c>[~]</c> the
        /// element is fine but an ancestor is hidden. The second case is the one that gets missed,
        /// because everything about the element reads correct in isolation.
        /// </summary>
        [CliCommand("uitk_tree",
            "Print the UI Toolkit element tree under a panel or element, marking what is hidden and why.")]
        public static object Tree(
            [CliArg("name", "Root element name. Omit for the whole panel.")] string name = null,
            [CliArg("document", "Restrict to documents whose GameObject name contains this.")] string document = null,
            [CliArg("depth", "Levels below the root to print.")] int depth = 4,
            [CliArg("classes", "Show the USS class list beside each entry.")] bool classes = false,
            [CliArg("size", "Show resolved width x height beside each entry.")] bool size = false,
            [CliArg("max_nodes", "Stop after this many entries.")] int maxNodes = 400)
        {
            if (depth < 0)
                depth = 0;
            if (maxNodes <= 0)
                maxNodes = 400;

            var panels = UitkPanels.All(document);
            if (panels.Count == 0)
                return new
                {
                    error = string.IsNullOrEmpty(document)
                        ? "No UIDocument with a built root was found."
                        : $"No document matched '{document}'. {UitkPanels.All().Count} document(s) are available.",
                };

            var roots = new List<(UitkPanels.Panel Panel, VisualElement Element)>();

            foreach (var panel in panels)
            {
                if (string.IsNullOrEmpty(name))
                {
                    roots.Add((panel, panel.Root));
                    continue;
                }

                foreach (var element in UitkPanels.Walk(panel.Root))
                {
                    if (element.name == name)
                        roots.Add((panel, element));
                }
            }

            if (roots.Count == 0)
                return new { error = $"No element named '{name}' in {panels.Count} document(s)." };

            var builder = new StringBuilder();
            var printed = 0;

            foreach (var (panel, element) in roots)
            {
                builder.Append("# ").Append(panel.Name);
                if (!panel.Active)
                    builder.Append("  (document disabled)");
                builder.Append('\n');

                Print(builder, element, 0, depth, classes, size, maxNodes, ref printed);

                if (printed >= maxNodes)
                    break;
            }

            return new
            {
                roots = roots.Count,
                nodes = printed,
                truncated = printed >= maxNodes,
                tree = builder.ToString(),
            };
        }

        static void Print(StringBuilder builder, VisualElement element, int level, int depth,
            bool classes, bool size, int maxNodes, ref int printed)
        {
            if (printed >= maxNodes)
                return;

            printed++;

            builder.Append(' ', level * 2);
            builder.Append(Marker(element)).Append(' ');
            builder.Append(string.IsNullOrEmpty(element.name) ? element.GetType().Name : element.name);

            if (!string.IsNullOrEmpty(element.name))
                builder.Append('<').Append(element.GetType().Name).Append('>');

            var text = UitkPanels.Text(element);
            if (!string.IsNullOrEmpty(text))
                builder.Append("  \"").Append(text).Append('"');

            if (classes)
            {
                var list = UitkPanels.Classes(element);
                if (!string.IsNullOrEmpty(list))
                    builder.Append("  .").Append(list.Replace(" ", " ."));
            }

            if (size)
            {
                var bounds = element.worldBound;
                builder.Append("  ").Append(bounds.width.ToString("0")).Append('x').Append(bounds.height.ToString("0"));
            }

            builder.Append('\n');

            if (level >= depth)
                return;

            var children = element.hierarchy;
            for (var i = 0; i < children.childCount; i++)
            {
                Print(builder, children[i], level + 1, depth, classes, size, maxNodes, ref printed);
                if (printed >= maxNodes)
                    return;
            }
        }

        /// <summary>
        /// <c>[-]</c> hidden here, <c>[~]</c> hidden by an ancestor, <c>[ ]</c> laid out. Separating the
        /// two hidden cases is the point: only the first is fixed on the element itself.
        /// </summary>
        static string Marker(VisualElement element)
        {
            if (element.resolvedStyle.display == DisplayStyle.None || !element.visible)
                return "[-]";

            return UitkPanels.HiddenAncestor(element) != null ? "[~]" : "[ ]";
        }
    }
}
