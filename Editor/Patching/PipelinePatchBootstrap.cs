using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MoreUpipe.Patching
{
    /// <summary>
    /// Reports pending patches once per Editor session.
    ///
    /// This exists because the patched files live in a cache Unity regenerates: after a package
    /// re-resolve the edits are gone with no other signal, and a command that silently stops behaving
    /// as configured is worse than a notice. The check reads the target files directly, so it reports
    /// what is on disk rather than what was installed at some earlier point.
    ///
    /// WHY it logs instead of opening a dialog: this package exists to let an automated caller drive
    /// the Editor, and <see cref="EditorUtility.DisplayDialog"/> is modal — it parks the main thread
    /// until a human clicks, which strands every main-thread command (eval, recompile, screenshot)
    /// with no response and no visible cause. A tool for unattended use must never block startup on
    /// human input. The console line carries the same information and costs nothing when nobody is
    /// watching.
    /// </summary>
    [InitializeOnLoad]
    static class PipelinePatchBootstrap
    {
        const string SessionKey = "MoreUpipe.PatchNoticeShown";

        static PipelinePatchBootstrap()
        {
            EditorApplication.delayCall += Check;
        }

        static void Check()
        {
            if (SessionState.GetBool(SessionKey, false))
                return;

            if (!PipelinePatchApplier.IsPipelineInstalled)
                return;

            var pending = PipelinePatchApplier.GetStatuses().Count(status => status.CanApply);
            if (pending == 0)
                return;

            SessionState.SetBool(SessionKey, true);

            Debug.Log($"[More Upipe] {pending} pipeline patch(es) available and not applied. " +
                      "Open Tools > More Upipe > Patch Manager to review.");
        }
    }
}
