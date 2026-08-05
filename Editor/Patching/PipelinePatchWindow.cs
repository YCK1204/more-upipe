using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MoreUpipe.Patching
{
    /// <summary>
    /// Patch manager window.
    ///
    /// Patches are opt-in per entry rather than applied wholesale on install. These edits change the
    /// behaviour of commands the host project may already be driving, so the person who owns that
    /// project decides which ones land, and can see exactly what each does before agreeing.
    /// </summary>
    public sealed class PipelinePatchWindow : EditorWindow
    {
        readonly Dictionary<string, Toggle> m_Toggles = new Dictionary<string, Toggle>();
        VisualElement m_List;
        Label m_Status;

        [MenuItem("Tools/More Upipe/Patch Manager")]
        public static void Open()
        {
            var window = GetWindow<PipelinePatchWindow>(true, "More Upipe — Patch Manager");
            window.minSize = new Vector2(560, 380);
            window.Show();
        }

        void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingLeft = 12;
            root.style.paddingRight = 12;
            root.style.paddingTop = 10;
            root.style.paddingBottom = 10;

            var title = new Label("Pipeline package patches");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 14;
            root.Add(title);

            m_Status = new Label { style = { marginTop = 4, marginBottom = 8, whiteSpace = WhiteSpace.Normal } };
            root.Add(m_Status);

            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            m_List = scroll.contentContainer;
            root.Add(scroll);

            var buttons = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 8 } };

            var applyButton = new Button(ApplySelected) { text = "Apply selected" };
            applyButton.style.flexGrow = 1;
            buttons.Add(applyButton);

            var revertButton = new Button(RevertSelected) { text = "Revert selected" };
            revertButton.style.flexGrow = 1;
            buttons.Add(revertButton);

            var refreshButton = new Button(Refresh) { text = "Refresh" };
            refreshButton.style.width = 90;
            buttons.Add(refreshButton);

            root.Add(buttons);

            Refresh();
        }

        void Refresh()
        {
            if (m_List == null)
                return;

            m_List.Clear();
            m_Toggles.Clear();

            if (!PipelinePatchApplier.IsPipelineInstalled)
            {
                m_Status.text = $"{PipelinePatchApplier.PipelinePackageName} is not installed. Nothing to patch.";
                return;
            }

            var installed = PipelinePatchApplier.InstalledPipelineVersion;
            m_Status.text = PipelinePatchApplier.IsUnverifiedPipelineVersion
                ? $"Installed pipeline {installed}. These patches were verified against {PipelinePatchApplier.VerifiedPipelineVersion} — " +
                  "each one is still checked against the file before it is applied, and refuses if the source moved."
                : $"Installed pipeline {installed}. Patched files live in Library/PackageCache, so a package re-resolve reverts them; " +
                  "re-open this window to re-apply.";

            foreach (var status in PipelinePatchApplier.GetStatuses())
                m_List.Add(BuildRow(status));
        }

        VisualElement BuildRow(PatchStatus status)
        {
            var box = new VisualElement
            {
                style =
                {
                    marginBottom = 8, paddingLeft = 8, paddingRight = 8, paddingTop = 6, paddingBottom = 6,
                    borderLeftWidth = 3, borderLeftColor = StateColor(status.State),
                    backgroundColor = new Color(0f, 0f, 0f, 0.12f),
                },
            };

            var toggle = new Toggle(status.Patch.Title)
            {
                value = status.CanApply || status.CanRevert,
            };
            toggle.SetEnabled(status.CanApply || status.CanRevert);
            m_Toggles[status.Patch.Id] = toggle;
            box.Add(toggle);

            var description = new Label(status.Patch.Description)
            {
                style = { whiteSpace = WhiteSpace.Normal, marginLeft = 18, opacity = 0.8f, fontSize = 11 },
            };
            box.Add(description);

            var stateText = status.State.ToString();
            if (!string.IsNullOrEmpty(status.Detail))
                stateText += " — " + status.Detail;

            var stateLabel = new Label(stateText)
            {
                style =
                {
                    whiteSpace = WhiteSpace.Normal, marginLeft = 18, marginTop = 2,
                    fontSize = 11, color = StateColor(status.State),
                },
            };
            box.Add(stateLabel);

            var path = new Label(status.Patch.TargetSummary)
            {
                style = { marginLeft = 18, marginTop = 2, fontSize = 10, opacity = 0.55f },
            };
            box.Add(path);

            return box;
        }

        static Color StateColor(PatchState state)
        {
            switch (state)
            {
                case PatchState.Applied: return new Color(0.45f, 0.8f, 0.45f);
                case PatchState.Applicable: return new Color(0.45f, 0.65f, 0.95f);
                case PatchState.AnchorNotFound:
                case PatchState.AnchorAmbiguous: return new Color(0.95f, 0.72f, 0.35f);
                default: return new Color(0.8f, 0.4f, 0.4f);
            }
        }

        IEnumerable<PipelinePatch> Selected()
        {
            return PipelinePatchCatalog.All.Where(patch =>
                m_Toggles.TryGetValue(patch.Id, out var toggle) && toggle.value);
        }

        void ApplySelected()
        {
            var selected = Selected().ToList();
            if (selected.Count == 0)
            {
                EditorUtility.DisplayDialog("More Upipe", "No patches selected.", "OK");
                return;
            }

            // Spell out that this writes into someone else's package before it happens.
            var confirmed = EditorUtility.DisplayDialog(
                "Apply pipeline patches",
                $"This edits {selected.Count} file(s) inside the installed com.unity.pipeline package.\n\n" +
                "A backup is written next to each file, and the changes are reverted whenever Unity " +
                "regenerates the package cache.\n\nContinue?",
                "Apply", "Cancel");

            if (!confirmed)
                return;

            var ok = PipelinePatchApplier.Apply(selected, out var report);
            EditorUtility.DisplayDialog(ok ? "Patches applied" : "Patch failed", report, "OK");
            Refresh();
        }

        void RevertSelected()
        {
            var selected = Selected().ToList();
            if (selected.Count == 0)
            {
                EditorUtility.DisplayDialog("More Upipe", "No patches selected.", "OK");
                return;
            }

            PipelinePatchApplier.Revert(selected, out var report);
            EditorUtility.DisplayDialog("Revert", report, "OK");
            Refresh();
        }
    }
}
