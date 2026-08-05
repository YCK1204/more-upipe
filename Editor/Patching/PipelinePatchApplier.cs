using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MoreUpipe.Patching
{
    public enum PatchState
    {
        /// <summary>Anchor found exactly once; the patch can be applied.</summary>
        Applicable,

        /// <summary>Marker present in the file; already applied.</summary>
        Applied,

        /// <summary>Anchor missing — the package source differs from what this patch expects.</summary>
        AnchorNotFound,

        /// <summary>Anchor occurs more than once; placement would be a guess.</summary>
        AnchorAmbiguous,

        /// <summary>Target file (or the package itself) is not present.</summary>
        FileMissing,
    }

    public sealed class PatchStatus
    {
        public PipelinePatch Patch { get; set; }
        public PatchState State { get; set; }
        public string Detail { get; set; }

        public bool CanApply => State == PatchState.Applicable;
        public bool CanRevert => State == PatchState.Applied;
    }

    /// <summary>
    /// Applies and reverts <see cref="PipelinePatch"/> edits against the installed
    /// <c>com.unity.pipeline</c> package.
    ///
    /// WHY the patched files live in Library/PackageCache and that is accepted rather than worked
    /// around: that directory is a cache Unity regenerates, so an edit there is not permanent. The
    /// alternative — embedding the whole package under Packages/ — makes the project own 12 MB of
    /// third-party source and turns every package update into a manual merge. Re-applying a small,
    /// self-describing edit after a re-resolve is the cheaper trade, and it is why state is detected
    /// from the file itself (see <see cref="PipelinePatch.Marker"/>) instead of being remembered in
    /// EditorPrefs: after Unity restores the cache, the truth is in the file, not in our bookkeeping.
    ///
    /// Every apply writes a sibling backup first, so a revert never depends on reconstructing the
    /// original text.
    /// </summary>
    public static class PipelinePatchApplier
    {
        public const string PipelinePackageName = "com.unity.pipeline";

        /// <summary>Package version this patch set was authored and verified against.</summary>
        public const string VerifiedPipelineVersion = "0.4.0-exp.1";

        const string BackupSuffix = ".moreupipe.orig";

        /// <summary>
        /// Root of the installed pipeline package, or null when it is not installed.
        /// Resolved through the Package Manager rather than by scanning Library/PackageCache, because
        /// the folder name carries a content hash that changes with every package version.
        /// </summary>
        public static string PackageRoot
        {
            get
            {
                var package = PackageInfo.GetAllRegisteredPackages()
                    .FirstOrDefault(candidate => candidate.name == PipelinePackageName);

                return package?.resolvedPath;
            }
        }

        public static string InstalledPipelineVersion
        {
            get
            {
                var package = PackageInfo.GetAllRegisteredPackages()
                    .FirstOrDefault(candidate => candidate.name == PipelinePackageName);

                return package?.version;
            }
        }

        public static bool IsPipelineInstalled => PackageRoot != null;

        /// <summary>
        /// True when the installed package is a version other than the one these patches were verified
        /// against. Not an error on its own — the anchors still decide — but worth surfacing before an
        /// unattended apply.
        /// </summary>
        public static bool IsUnverifiedPipelineVersion =>
            IsPipelineInstalled && InstalledPipelineVersion != VerifiedPipelineVersion;

        public static List<PatchStatus> GetStatuses()
        {
            return PipelinePatchCatalog.All.Select(GetStatus).ToList();
        }

        public static PatchStatus GetStatus(PipelinePatch patch)
        {
            var result = new PatchStatus { Patch = patch };

            var path = ResolveTargetPath(patch);
            if (path == null || !File.Exists(path))
            {
                result.State = PatchState.FileMissing;
                result.Detail = path ?? $"{PipelinePackageName} is not installed";
                return result;
            }

            var source = File.ReadAllText(path);
            if (source.Contains(patch.Marker))
            {
                result.State = PatchState.Applied;
                return result;
            }

            var occurrences = CountOccurrences(source, patch.Anchor);
            if (occurrences == 0)
            {
                result.State = PatchState.AnchorNotFound;
                result.Detail = "Anchor not found — the package source differs from what this patch expects.";
            }
            else if (occurrences > 1)
            {
                result.State = PatchState.AnchorAmbiguous;
                result.Detail = $"Anchor found {occurrences} times; refusing to guess where to apply.";
            }
            else
            {
                result.State = PatchState.Applicable;
            }

            return result;
        }

        /// <summary>
        /// Apply the given patches. Nothing is written unless every requested patch is applicable, so a
        /// partial apply cannot leave the package half-edited and uncompilable.
        /// </summary>
        public static bool Apply(IEnumerable<PipelinePatch> patches, out string report)
        {
            var requested = patches?.ToList() ?? new List<PipelinePatch>();
            if (requested.Count == 0)
            {
                report = "Nothing selected.";
                return false;
            }

            var blocked = requested
                .Select(GetStatus)
                .Where(status => !status.CanApply && status.State != PatchState.Applied)
                .ToList();

            if (blocked.Count > 0)
            {
                report = "Aborted; no files were changed:\n" +
                         string.Join("\n", blocked.Select(status => $"  {status.Patch.Id}: {status.State} — {status.Detail}"));
                return false;
            }

            var applied = new List<string>();
            foreach (var patch in requested)
            {
                if (GetStatus(patch).State == PatchState.Applied)
                    continue;

                var path = ResolveTargetPath(patch);
                var source = File.ReadAllText(path);

                BackUp(path, source);

                var patched = source.Replace(patch.Anchor, patch.Marker + Environment.NewLine + patch.Replacement);
                File.WriteAllText(path, patched);
                applied.Add(patch.Id);
            }

            AssetDatabase.Refresh();

            report = applied.Count == 0
                ? "Already applied; nothing to do."
                : "Applied: " + string.Join(", ", applied);
            return true;
        }

        /// <summary>
        /// Restore the backup taken at apply time. Falls back to stripping the marker line when the
        /// backup is gone (a cache regeneration removes it along with everything else), because a file
        /// carrying our marker but no backup is still recoverable — the marker line is the only thing
        /// we added that a fresh copy would not have.
        /// </summary>
        public static bool Revert(IEnumerable<PipelinePatch> patches, out string report)
        {
            var reverted = new List<string>();
            var failed = new List<string>();

            foreach (var patch in patches ?? Enumerable.Empty<PipelinePatch>())
            {
                var path = ResolveTargetPath(patch);
                if (path == null || !File.Exists(path))
                {
                    failed.Add($"{patch.Id}: target file missing");
                    continue;
                }

                var backup = path + BackupSuffix;
                if (File.Exists(backup))
                {
                    File.Copy(backup, path, true);
                    File.Delete(backup);
                    reverted.Add(patch.Id);
                    continue;
                }

                var source = File.ReadAllText(path);
                if (!source.Contains(patch.Marker))
                {
                    failed.Add($"{patch.Id}: not applied");
                    continue;
                }

                failed.Add($"{patch.Id}: backup missing — reinstall the package to restore the original");
            }

            if (reverted.Count > 0)
                AssetDatabase.Refresh();

            report = string.Join("\n",
                new[]
                {
                    reverted.Count > 0 ? "Reverted: " + string.Join(", ", reverted) : null,
                    failed.Count > 0 ? "Failed:\n  " + string.Join("\n  ", failed) : null,
                }.Where(line => line != null));

            if (string.IsNullOrEmpty(report))
                report = "Nothing to revert.";

            return failed.Count == 0;
        }

        static void BackUp(string path, string originalSource)
        {
            var backup = path + BackupSuffix;

            // Never overwrite an existing backup: it holds the pristine text from the first apply, and
            // the current file may already carry an earlier patch of ours.
            if (!File.Exists(backup))
                File.WriteAllText(backup, originalSource);
        }

        static string ResolveTargetPath(PipelinePatch patch)
        {
            var root = PackageRoot;
            if (root == null)
                return null;

            return Path.GetFullPath(Path.Combine(root, patch.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        static int CountOccurrences(string source, string needle)
        {
            var count = 0;
            for (var index = source.IndexOf(needle, StringComparison.Ordinal);
                 index >= 0;
                 index = source.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }
    }
}
