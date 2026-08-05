using System.Collections.Generic;
using System.Linq;
using Unity.Pipeline.Commands;
using UnityEngine;
using UnityEngine.EventSystems;
using Object = UnityEngine.Object;

namespace MoreUpipe
{
    /// <summary>
    /// UI click driven through <see cref="ExecuteEvents"/>, plus a raycast probe for diagnosing
    /// clicks that never arrive.
    ///
    /// WHY not simulate_pointer: that command feeds the Input System, which a project running the
    /// legacy <c>StandaloneInputModule</c> ignores outright — the pointer event is dispatched and
    /// nothing happens. Sending the pointer events directly to the target works regardless of which
    /// input backend the project uses.
    ///
    /// Note the trade-off this makes: dispatching straight to the object bypasses the raycast, so a
    /// button covered by another graphic still "clicks" here while a real user could not press it.
    /// That is why <see cref="Raycast"/> exists — it answers what actually sits under a screen point,
    /// which is the question behind every "the button does nothing" report.
    /// </summary>
    public static class UiClickCommand
    {
        /// <summary>
        /// Click a UI object by hierarchy path or name.
        ///
        /// Pointer down / up / click are sent in that order because many widgets latch state on
        /// <see cref="ExecuteEvents.pointerDownHandler"/> and only commit on the click, so firing the
        /// click alone leaves them half-driven. Inactive objects are rejected rather than clicked:
        /// clicking something the player cannot see is a false positive that hides the real bug.
        /// </summary>
        [CliCommand("ui_click", "Click a UI object by path or name via ExecuteEvents (works with the legacy input module).")]
        public static object Click(
            [CliArg("path", "Full hierarchy path ('Root/Child/Button'). Takes precedence over name.")] string path = null,
            [CliArg("name", "Object name. Used when no path is given; ambiguous names are reported instead of guessed.")] string name = null)
        {
            if (string.IsNullOrEmpty(path) && string.IsNullOrEmpty(name))
                return new { error = "Supply at least one of 'path' or 'name'." };

            var matches = FindTargets(path, name);
            if (matches.Count == 0)
                return new { error = "No object matched.", path, name };

            // Ambiguity is reported, never resolved by picking the first hit: the same UI name repeats
            // across canvases, and silently clicking the wrong one is worse than not clicking.
            if (matches.Count > 1)
                return new
                {
                    error = "Multiple objects matched. Pass an exact 'path'.",
                    candidates = matches.Select(HierarchyPath).ToArray(),
                };

            var target = matches[0];
            if (!target.gameObject.activeInHierarchy)
                return new { error = "Target is inactive.", path = HierarchyPath(target), activeSelf = target.gameObject.activeSelf };

            var pointer = new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left,
                position = ScreenPointOf(target),
            };

            var down = ExecuteEvents.Execute(target.gameObject, pointer, ExecuteEvents.pointerDownHandler);
            var up = ExecuteEvents.Execute(target.gameObject, pointer, ExecuteEvents.pointerUpHandler);
            var click = ExecuteEvents.Execute(target.gameObject, pointer, ExecuteEvents.pointerClickHandler);

            // A handler-less object reports false on every channel; surfacing that is more useful than
            // a bare "ok", because it distinguishes "clicked" from "clicked nothing".
            return new
            {
                path = HierarchyPath(target),
                handled = down || up || click,
                pointerDown = down,
                pointerUp = up,
                pointerClick = click,
            };
        }

        /// <summary>
        /// List everything the UI raycaster hits at a screen point, topmost first.
        ///
        /// This is the diagnostic for a click that goes nowhere: the input module dispatches to the
        /// first hit, so anything sitting above the intended target — a full-screen cover, a loading
        /// overlay, a stray raycast-enabled image — shows up here as entry zero.
        /// </summary>
        [CliCommand("ui_raycast", "List UI objects under a screen point, topmost first. Shows what intercepts a click.")]
        public static object Raycast(
            [CliArg("x", "Screen X in pixels, origin bottom-left.")] float x = 0f,
            [CliArg("y", "Screen Y in pixels, origin bottom-left.")] float y = 0f)
        {
            if (EventSystem.current == null)
                return new { error = "No EventSystem in the scene." };

            var pointer = new PointerEventData(EventSystem.current) { position = new Vector2(x, y) };
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(pointer, hits);

            return new
            {
                point = new { x, y },
                count = hits.Count,
                hits = hits.Select(hit => new
                {
                    path = HierarchyPath(hit.gameObject.transform),
                    name = hit.gameObject.name,
                    sortingOrder = hit.sortingOrder,
                    depth = hit.depth,
                    module = hit.module != null ? hit.module.GetType().Name : null,
                }).ToArray(),
            };
        }

        static List<Transform> FindTargets(string path, string name)
        {
            var all = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            if (!string.IsNullOrEmpty(path))
                return all.Where(transform => HierarchyPath(transform) == path).ToList();

            return all.Where(transform => transform.name == name).ToList();
        }

        /// <summary>
        /// Screen position of the object's rect centre, used to fill the pointer event. Handlers that
        /// read <see cref="PointerEventData.position"/> (drag thresholds, tooltips) misbehave when it
        /// is left at zero.
        /// </summary>
        static Vector2 ScreenPointOf(Transform target)
        {
            var rect = target as RectTransform;
            if (rect == null)
                return Vector2.zero;

            var canvas = target.GetComponentInParent<Canvas>();
            var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera
                : null;

            return RectTransformUtility.WorldToScreenPoint(camera, rect.position);
        }

        static string HierarchyPath(Transform target)
        {
            var path = target.name;
            for (var cursor = target.parent; cursor != null; cursor = cursor.parent)
                path = cursor.name + "/" + path;

            return path;
        }
    }
}
