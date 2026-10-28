using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace MoreUpipe
{
    /// <summary>
    /// Shared lookup for the runtime UI Toolkit panels in the loaded scenes, plus the element helpers
    /// the <c>uitk *</c> commands agree on.
    ///
    /// WHY panels are reached through <see cref="UIDocument"/> rather than the panel registry: the
    /// registry (<c>UIElementsRuntimeUtility</c>) is internal, so any access is reflection that breaks
    /// on a Unity upgrade. Every runtime panel is owned by a <see cref="UIDocument"/> component, which
    /// is public API and additionally carries the GameObject name — the handle a caller already knows
    /// from the hierarchy. Inactive documents are included on purpose: "the panel is switched off" is
    /// one of the answers being looked for, and excluding them would report it as "not found".
    /// </summary>
    internal static class UitkPanels
    {
        /// <summary>One runtime panel: the document that owns it and its root element.</summary>
        internal readonly struct Panel
        {
            internal readonly UIDocument Document;
            internal readonly VisualElement Root;

            internal Panel(UIDocument document, VisualElement root)
            {
                Document = document;
                Root = root;
            }

            internal string Name => Document != null ? Document.gameObject.name : "(unknown)";

            internal bool Active => Document != null && Document.isActiveAndEnabled;
        }

        /// <summary>
        /// Every document-backed panel with a live root, optionally narrowed to one document.
        ///
        /// <paramref name="document"/> matches the owning GameObject's name as a case-insensitive
        /// substring. A document whose root is null is skipped rather than reported: it has not been
        /// built yet, so it holds no elements to answer questions about.
        /// </summary>
        internal static List<Panel> All(string document = null)
        {
            var panels = new List<Panel>();

            foreach (var doc in SceneObjects.All<UIDocument>())
            {
                if (doc == null)
                    continue;

                if (!string.IsNullOrEmpty(document) &&
                    doc.gameObject.name.IndexOf(document, System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var root = doc.rootVisualElement;
                if (root == null)
                    continue;

                panels.Add(new Panel(doc, root));
            }

            return panels;
        }

        /// <summary>
        /// Depth-first walk over an element and its descendants, using
        /// <see cref="VisualElement.hierarchy"/> so that composite widgets expose their internal parts.
        ///
        /// WHY hierarchy and not <c>Children()</c>: a control such as Toggle or ScrollView keeps its
        /// generated pieces (the checkmark, the scroller) outside the content container. Those pieces
        /// are exactly what a caller is looking for when a widget renders wrong, and
        /// <c>Children()</c> hides them.
        /// </summary>
        internal static IEnumerable<VisualElement> Walk(VisualElement root)
        {
            if (root == null)
                yield break;

            var stack = new Stack<VisualElement>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var element = stack.Pop();
                yield return element;

                var children = element.hierarchy;
                for (var i = children.childCount - 1; i >= 0; i--)
                    stack.Push(children[i]);
            }
        }

        /// <summary>
        /// Slash-separated path from the panel root, using each element's name and falling back to its
        /// type when unnamed. Unnamed elements are common in UI Toolkit, so a path built from names
        /// alone would collapse into a run of empty segments.
        /// </summary>
        internal static string Path(VisualElement element)
        {
            var parts = new List<string>();

            for (var current = element; current != null; current = current.hierarchy.parent)
                parts.Add(string.IsNullOrEmpty(current.name) ? current.GetType().Name : current.name);

            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>
        /// Whether the element is laid out and visible, and — when it is not — which ancestor turned it
        /// off. <c>display: none</c> removes an element from layout entirely, so an element can be
        /// perfectly configured and still absent because a parent is hidden.
        /// </summary>
        internal static VisualElement HiddenAncestor(VisualElement element)
        {
            for (var current = element?.hierarchy.parent; current != null; current = current.hierarchy.parent)
            {
                if (current.resolvedStyle.display == DisplayStyle.None || !current.visible)
                    return current;
            }

            return null;
        }

        /// <summary>Visible text of an element, for the text-bearing controls. Empty otherwise.</summary>
        internal static string Text(VisualElement element)
        {
            return element is TextElement text ? text.text : null;
        }

        /// <summary>USS class list as a single space-separated string, or null when the element has none.</summary>
        internal static string Classes(VisualElement element)
        {
            var list = element?.GetClasses();
            if (list == null)
                return null;

            var joined = string.Join(" ", list);
            return string.IsNullOrEmpty(joined) ? null : joined;
        }
    }
}
