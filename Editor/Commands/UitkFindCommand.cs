using System.Collections.Generic;
using Unity.Pipeline.Commands;
using UnityEngine.UIElements;

namespace MoreUpipe
{
    /// <summary>
    /// Locate UI Toolkit elements by name, USS class or visible text, across every runtime panel.
    ///
    /// WHY the <c>ui_*</c> commands do not cover this: they address uGUI — <c>GameObject</c>,
    /// <c>UI.Text</c>, <c>GraphicRaycaster</c>. A UI Toolkit element is not a GameObject, carries no
    /// component, and never appears in the scene hierarchy, so <c>ui_find</c> and
    /// <c>get_scene_hierarchy</c> both report a project's entire UI as a handful of
    /// <c>UIDocument</c> objects with nothing inside.
    ///
    /// WHY not eval: the equivalent is a <see cref="UQueryExtensions"/> sweep plus a parent walk, and
    /// the wrapper eval compiles against has no <c>UnityEngine.UIElements</c> using, so it has to be
    /// written fully qualified every time. That is a dozen lines re-sent per question.
    ///
    /// The response reports <c>display</c> and the ancestor that hid the element, because
    /// <c>display: none</c> propagates: an element can be correct in every other respect and still be
    /// absent from the screen because a parent is switched off.
    /// </summary>
    public static class UitkFindCommand
    {
        /// <summary>
        /// Find elements matching any combination of name, USS class and text.
        ///
        /// At least one filter is required; supplying several requires the element to satisfy each.
        /// Name and text match as substrings unless <paramref name="exact"/> is set;
        /// <paramref name="className"/> is always an exact class-list membership test, because USS
        /// class names are a fixed vocabulary and a substring match on them mostly returns modifiers of
        /// unrelated blocks.
        /// </summary>
        [CliCommand("uitk_find",
            "Find UI Toolkit elements by name, USS class or text. Reports layout, display and which ancestor hid them.")]
        public static object Find(
            [CliArg("name", "Element name to match. Substring unless exact=true.")] string name = null,
            [CliArg("class", "USS class the element must carry. Exact match.")] string className = null,
            [CliArg("text", "Visible text to match. Substring unless exact=true.")] string text = null,
            [CliArg("document", "Restrict to documents whose GameObject name contains this.")] string document = null,
            [CliArg("exact", "Require exact matches for name and text.")] bool exact = false,
            [CliArg("limit", "Maximum number of results to return.")] int limit = 30)
        {
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(className) && string.IsNullOrEmpty(text))
                return new { error = "Supply at least one of 'name', 'class' or 'text'." };

            if (limit <= 0)
                limit = 30;

            var panels = UitkPanels.All(document);
            if (panels.Count == 0)
                return new
                {
                    // Separating these matters: "no panels at all" is a different problem from "your
                    // filter excluded them", and reporting the first for the second sends the caller
                    // looking for a missing UIDocument that is right there.
                    error = string.IsNullOrEmpty(document)
                        ? "No UIDocument with a built root was found."
                        : $"No document matched '{document}'. {UitkPanels.All().Count} document(s) are available.",
                    count = 0,
                };

            var results = new List<object>();
            var truncated = false;

            foreach (var panel in panels)
            {
                foreach (var element in UitkPanels.Walk(panel.Root))
                {
                    if (!string.IsNullOrEmpty(name) && !Matches(element.name, name, exact))
                        continue;

                    if (!string.IsNullOrEmpty(className) && !element.ClassListContains(className))
                        continue;

                    var elementText = UitkPanels.Text(element);
                    if (!string.IsNullOrEmpty(text) && !Matches(elementText, text, exact))
                        continue;

                    // Checked after the filters, not before: testing the cap on every element would
                    // report truncation whenever the limit was reached, even if nothing further
                    // matched. `truncated` is a claim that results were dropped, so it may only be set
                    // by a match that actually was.
                    if (results.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }

                    var hiddenBy = UitkPanels.HiddenAncestor(element);
                    var bounds = element.worldBound;

                    results.Add(new
                    {
                        document = panel.Name,
                        path = UitkPanels.Path(element),
                        name = string.IsNullOrEmpty(element.name) ? null : element.name,
                        type = element.GetType().Name,
                        classes = UitkPanels.Classes(element),
                        text = elementText,
                        display = element.resolvedStyle.display.ToString(),
                        // NaN until the panel has laid out once; reported as-is so "never measured"
                        // stays distinguishable from "measured as zero".
                        width = bounds.width,
                        height = bounds.height,
                        enabled = element.enabledSelf,
                        hiddenBy = hiddenBy == null ? null : UitkPanels.Path(hiddenBy),
                    });
                }

                if (truncated)
                    break;
            }

            return new
            {
                count = results.Count,
                truncated,
                documentsSearched = panels.Count,
                results,
            };
        }

        static bool Matches(string value, string needle, bool exact)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            return exact
                ? value == needle
                : value.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
