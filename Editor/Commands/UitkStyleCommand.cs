using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine.UIElements;

namespace MoreUpipe
{
    /// <summary>
    /// Read an element's resolved style and layout box, after letting the panel lay out.
    ///
    /// WHY the wait is part of the command: UI Toolkit resolves layout on the next frame, so a value
    /// read in the same call that changed a style, added a class or toggled display is stale — usually
    /// zero or NaN. Through eval this shows up as a measurement that disagrees with the screen, and the
    /// workaround is to issue the same query twice and trust the second answer. Waiting here makes the
    /// first answer the correct one, and removes an entire class of "the layout is broken" reports that
    /// were only a measurement taken too early.
    ///
    /// The distinction the response preserves: <c>resolvedStyle</c> is what the style system computed,
    /// <c>worldBound</c> is where the element actually sits on screen. They disagree when a parent
    /// clips, scales or shrinks the element, and that disagreement is the bug being looked for.
    /// </summary>
    public static class UitkStyleCommand
    {
        /// <summary>
        /// Resolve one element and report its layout box plus the style properties that decide it.
        ///
        /// Ambiguous names are reported rather than resolved to the first hit: element names repeat
        /// across documents, and measuring the wrong one produces a confident answer about the wrong
        /// element. Pass <paramref name="document"/> to disambiguate.
        /// </summary>
        [CliCommand("uitk.style",
            "Read a UI Toolkit element's resolved style and layout box, after waiting for the panel to lay out.",
            MainThreadRequired = false)]
        public static async Task<object> Style(
            [CliArg("name", "Element name to resolve.", Required = true)] string name = null,
            [CliArg("document", "Restrict to documents whose GameObject name contains this.")] string document = null,
            [CliArg("props", "Comma-separated property names to include. Omit for the common layout set.")] string props = null,
            [CliArg("await_frames", "Frames to let pass before measuring. 0 measures immediately.")] int awaitFrames = 1)
        {
            if (string.IsNullOrEmpty(name))
                return new { error = "'name' is required." };

            if (awaitFrames > 0)
                await SkipFrames(awaitFrames);

            return await OnMainThread(() =>
            {
                var matches = new List<(UitkPanels.Panel Panel, VisualElement Element)>();

                foreach (var panel in UitkPanels.All(document))
                foreach (var element in UitkPanels.Walk(panel.Root))
                {
                    if (element.name == name)
                        matches.Add((panel, element));
                }

                if (matches.Count == 0)
                    return new { error = $"No element named '{name}'." };

                if (matches.Count > 1)
                {
                    // Paths are long, and a name shared by a generated part can match dozens. Listing
                    // them all would spend more than the answer is worth on a message whose point is
                    // "narrow it down" — a handful is enough to see the pattern.
                    const int shown = 5;
                    var sample = matches
                        .Take(shown)
                        .Select(m => new { document = m.Panel.Name, path = UitkPanels.Path(m.Element) })
                        .ToArray();

                    var documents = matches.Select(m => m.Panel.Name).Distinct().ToArray();

                    return new
                    {
                        error = $"'{name}' matches {matches.Count} elements. Narrow it down before measuring.",
                        // 'document' only helps when the matches are spread across documents; when they
                        // are not, saying so saves the caller a round-trip that could not have worked.
                        hint = documents.Length > 1
                            ? $"Pass document= to pick one of: {string.Join(", ", documents)}"
                            : "All matches are in one document; this name is not unique enough to address.",
                        matchCount = matches.Count,
                        showing = sample.Length,
                        matches = sample,
                    };
                }

                var (owner, target) = matches[0];
                var wanted = Parse(props);
                var style = target.resolvedStyle;
                var bounds = target.worldBound;
                var hiddenBy = UitkPanels.HiddenAncestor(target);

                var values = new Dictionary<string, object>();
                void Report(string key, object value)
                {
                    if (wanted == null || wanted.Contains(key))
                        values[key] = value;
                }

                Report("display", style.display.ToString());
                Report("visibility", style.visibility.ToString());
                Report("opacity", style.opacity);
                Report("position", style.position.ToString());
                Report("left", style.left);
                Report("top", style.top);
                Report("right", style.right);
                Report("bottom", style.bottom);
                Report("width", style.width);
                Report("height", style.height);
                Report("flexGrow", style.flexGrow);
                Report("flexShrink", style.flexShrink);
                Report("flexDirection", style.flexDirection.ToString());
                Report("alignItems", style.alignItems.ToString());
                Report("alignSelf", style.alignSelf.ToString());
                Report("justifyContent", style.justifyContent.ToString());
                Report("marginTop", style.marginTop);
                Report("marginBottom", style.marginBottom);
                Report("marginLeft", style.marginLeft);
                Report("marginRight", style.marginRight);
                Report("paddingTop", style.paddingTop);
                Report("paddingBottom", style.paddingBottom);
                Report("paddingLeft", style.paddingLeft);
                Report("paddingRight", style.paddingRight);
                Report("borderTopWidth", style.borderTopWidth);
                Report("borderBottomWidth", style.borderBottomWidth);
                Report("borderLeftWidth", style.borderLeftWidth);
                Report("borderRightWidth", style.borderRightWidth);
                Report("backgroundColor", style.backgroundColor.ToString());
                Report("color", style.color.ToString());
                Report("fontSize", style.fontSize);
                Report("unityTextAlign", style.unityTextAlign.ToString());

                return new
                {
                    document = owner.Name,
                    path = UitkPanels.Path(target),
                    type = target.GetType().Name,
                    classes = UitkPanels.Classes(target),
                    text = UitkPanels.Text(target),
                    enabled = target.enabledSelf,
                    // Where it actually sits, which resolvedStyle does not answer once a parent clips
                    // or shrinks it.
                    worldBound = new { x = bounds.x, y = bounds.y, width = bounds.width, height = bounds.height },
                    hiddenBy = hiddenBy == null ? null : UitkPanels.Path(hiddenBy),
                    awaitedFrames = awaitFrames,
                    style = values,
                };
            });
        }

        static HashSet<string> Parse(string props)
        {
            if (string.IsNullOrWhiteSpace(props))
                return null;

            return new HashSet<string>(
                props.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0),
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Let <paramref name="frames"/> editor updates pass. Layout is resolved during the panel's
        /// update, so a measurement taken before one has run reports the previous frame's box.
        /// </summary>
        static Task SkipFrames(int frames)
        {
            var completion = new TaskCompletionSource<bool>();
            var remaining = frames;

            void Tick()
            {
                if (--remaining > 0)
                    return;

                EditorApplication.update -= Tick;
                completion.SetResult(true);
            }

            EditorApplication.update += Tick;
            return completion.Task;
        }

        /// <summary>
        /// Run <paramref name="work"/> on the main thread. The command is declared
        /// <c>MainThreadRequired = false</c> so the frame wait does not block the Editor, which means
        /// the measurement itself has to be marshalled back.
        /// </summary>
        static Task<object> OnMainThread(Func<object> work)
        {
            var completion = new TaskCompletionSource<object>();

            void Tick()
            {
                EditorApplication.update -= Tick;
                try
                {
                    completion.SetResult(work());
                }
                catch (Exception exception)
                {
                    completion.SetResult(new { error = exception.Message });
                }
            }

            EditorApplication.update += Tick;
            return completion.Task;
        }
    }
}
