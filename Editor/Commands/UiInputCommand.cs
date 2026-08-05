using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Pipeline.Commands;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreUpipe
{
    /// <summary>
    /// Set the text of an input field.
    ///
    /// WHY this is a command rather than one more eval: assigning <c>.text</c> is not enough. A field
    /// that only has its backing string replaced never raises <c>onValueChanged</c> or
    /// <c>onEndEdit</c>, so validation, "submit" buttons and view-model bindings never learn the value
    /// changed — the text is on screen and the game still behaves as if the field were empty. Getting
    /// that right by hand means re-sending the same lookup, the same component branch and the same two
    /// invoke calls every single time.
    ///
    /// Both <c>UnityEngine.UI.InputField</c> and TextMeshPro's <c>TMP_InputField</c> are supported. TMP
    /// is reached through reflection so this compiles in projects without the TMP package.
    /// </summary>
    public static class UiInputCommand
    {
        /// <summary>
        /// Assign text to an input field and fire its change callbacks.
        ///
        /// Ambiguous names are reported rather than resolved, for the same reason
        /// <c>ui_click</c> refuses them: typing into the wrong field of a login form looks like a
        /// success and fails somewhere else entirely.
        /// </summary>
        [CliCommand("ui_input", "Set an InputField / TMP_InputField's text and fire its change callbacks.")]
        public static object SetText(
            [CliArg("path", "Full hierarchy path of the input field. Takes precedence over name.")] string path = null,
            [CliArg("name", "Object name. Used when no path is given.")] string name = null,
            [CliArg("text", "Text to assign.", Required = true)] string text = null,
            [CliArg("submit", "Also fire onEndEdit / onSubmit after assigning.")] bool submit = true)
        {
            if (string.IsNullOrEmpty(path) && string.IsNullOrEmpty(name))
                return new { error = "Supply at least one of 'path' or 'name'." };

            var matches = FindFields(path, name);
            if (matches.Count == 0)
                return new { error = "No input field matched.", path, name };

            if (matches.Count > 1)
                return new
                {
                    error = "Multiple input fields matched. Pass an exact 'path'.",
                    candidates = matches.Select(HierarchyPath).ToArray(),
                };

            var target = matches[0];
            text ??= string.Empty;

            var legacy = target.GetComponent<UnityEngine.UI.InputField>();
            if (legacy != null)
            {
                legacy.text = text;
                legacy.onValueChanged?.Invoke(text);
                if (submit)
                    legacy.onEndEdit?.Invoke(text);

                return new { path = HierarchyPath(target), kind = "InputField", text = legacy.text };
            }

            var tmp = target.GetComponents<Component>()
                .FirstOrDefault(component => component != null && component.GetType().Name == "TMP_InputField");

            if (tmp == null)
                return new { error = "Object carries no InputField or TMP_InputField.", path = HierarchyPath(target) };

            var type = tmp.GetType();
            type.GetProperty("text")?.SetValue(tmp, text);
            InvokeUnityEvent(tmp, type, "onValueChanged", text);
            if (submit)
            {
                InvokeUnityEvent(tmp, type, "onEndEdit", text);
                InvokeUnityEvent(tmp, type, "onSubmit", text);
            }

            return new { path = HierarchyPath(target), kind = "TMP_InputField", text = type.GetProperty("text")?.GetValue(tmp) as string };
        }

        /// <summary>
        /// Fire a UnityEvent&lt;string&gt; exposed as a property, when the TMP version in use has it.
        /// Older TMP releases lack <c>onSubmit</c>, so a missing event is skipped rather than treated
        /// as an error.
        /// </summary>
        static void InvokeUnityEvent(object instance, System.Type type, string propertyName, string argument)
        {
            var evt = type.GetProperty(propertyName)?.GetValue(instance);
            if (evt == null)
                return;

            var invoke = evt.GetType().GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(string) }, null);

            invoke?.Invoke(evt, new object[] { argument });
        }

        static List<Transform> FindFields(string path, string name)
        {
            var all = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);

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
