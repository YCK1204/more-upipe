using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace MoreUpipe.Patching
{
    public enum PatchState
    {
        /// <summary>Every edit's anchor was found exactly once; the patch can be applied.</summary>
        Applicable,

        /// <summary>Marker present in every target file; already applied.</summary>
        Applied,

        /// <summary>An anchor is missing — the package source differs from what this patch expects.</summary>
        AnchorNotFound,

        /// <summary>An anchor occurs more than once; placement would be a guess.</summary>
        AnchorAmbiguous,

        /// <summary>A target file (or the package itself) is not present.</summary>
        FileMissing,

        /// <summary>Some files carry the marker and others do not — a previous run was interrupted.</summary>
        PartiallyApplied,
    }

    public sealed class PatchStatus
    {
        public PipelinePatch Patch { get; set; }
        public PatchState State { get; set; }
        public string Detail { get; set; }

        public bool CanApply => State == PatchState.Applicable;
        public bool CanRevert => State == PatchState.Applied || State == PatchState.PartiallyApplied;
    }

    /// <summary>
    /// Applies and reverts <see cref="PipelinePatch"/> edits against the installed
    /// <c>com.unity.pipeline</c> package.
    ///
    /// WHY the patched files live in Library/PackageCache and that is accepted rather than worked
    /// around: that directory is a cache Unity regenerates, so an edit there is not permanent. The
    /// alternative — embedding the whole package under Packages/ — makes the project own 12 MB of
    /// third-party source and turns every package update into a manual merge. Re-applying a small,
    /// self-describing edit after a re-resolve is the cheaper trade, and it is why state is read from
    /// the files themselves (see <see cref="PipelinePatch.Marker"/>) rather than remembered in
    /// EditorPrefs: once Unity restores the cache, the truth is in the file, not in our bookkeeping.
    ///
    /// Every apply writes a sibling backup first, so a revert never depends on reconstructing the
    /// original text.
    /// </summary>
    public static class PipelinePatchApplier
    {
        public const string PipelinePackageName = "com.unity.pipeline";

        /// <summary>Package version this patch set was authored and verified against.</summary>
        public const string VerifiedPipelineVersion = "0.9.1-exp.1";

        const string BackupSuffix = ".moreupipe.orig";

        /// <summary>
        /// Root of the installed pipeline package, or null when it is not installed.
        ///
        /// Resolved by asking the filesystem to expand the virtual <c>Packages/&lt;name&gt;</c> path,
        /// not by scanning Library/PackageCache (the folder name carries a content hash that changes
        /// with every version) and NOT through the Package Manager API.
        ///
        /// WHY not the Package Manager: <c>PackageInfo.GetAllRegisteredPackages()</c> reaches into UPM,
        /// and calling it early in a domain reload — which is when a startup check runs — makes Unity
        /// construct <c>PackageManagerProjectSettings</c> and <c>ServicesContainer</c> while they are
        /// already being constructed, logging "ScriptableSingleton already exists. Did you query the
        /// singleton in a constructor?" for each. The warnings are Unity's, but the trigger is ours,
        /// and a diagnostic tool that adds noise to the console it is meant to help read is working
        /// against itself. Path expansion needs none of that machinery.
        /// </summary>
        public static string PackageRoot
        {
            get
            {
                if (s_PackageRoot != null)
                    return s_PackageRoot.Length == 0 ? null : s_PackageRoot;

                string resolved = null;
                try
                {
                    var candidate = Path.GetFullPath("Packages/" + PipelinePackageName);
                    if (Directory.Exists(candidate))
                        resolved = candidate;
                }
                catch (Exception)
                {
                    resolved = null;
                }

                // Empty string means "looked, not there" — distinct from null, which means "not looked
                // yet". Without that distinction a missing package is re-probed on every access.
                s_PackageRoot = resolved ?? string.Empty;
                return resolved;
            }
        }

        /// <summary>Version string from the installed package's manifest, or null when unreadable.</summary>
        public static string InstalledPipelineVersion
        {
            get
            {
                if (s_Version != null)
                    return s_Version.Length == 0 ? null : s_Version;

                s_Version = ReadPackageVersion() ?? string.Empty;
                return s_Version.Length == 0 ? null : s_Version;
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

        // Cached because GetStatus resolves a path per edit, so a single status sweep would otherwise
        // hit the filesystem a dozen times for an answer that cannot change without a domain reload.
        static string s_PackageRoot;
        static string s_Version;

        /// <summary>
        /// Pull "version" out of the package manifest. Hand-scanned rather than JSON-parsed to avoid
        /// taking a dependency on a serializer for one field.
        /// </summary>
        static string ReadPackageVersion()
        {
            var root = PackageRoot;
            if (root == null)
                return null;

            try
            {
                var manifest = Path.Combine(root, "package.json");
                if (!File.Exists(manifest))
                    return null;

                foreach (var line in File.ReadAllLines(manifest))
                {
                    var key = line.IndexOf("\"version\"", StringComparison.Ordinal);
                    if (key < 0)
                        continue;

                    var open = line.IndexOf('"', line.IndexOf(':', key) + 1);
                    var close = open < 0 ? -1 : line.IndexOf('"', open + 1);
                    if (open >= 0 && close > open)
                        return line.Substring(open + 1, close - open - 1);
                }
            }
            catch (Exception)
            {
                // An unreadable manifest is not worth failing over: the anchors decide applicability,
                // and the version is only shown as context.
            }

            return null;
        }

        public static List<PatchStatus> GetStatuses()
        {
            return PipelinePatchCatalog.All.Select(GetStatus).ToList();
        }

        public static PatchStatus GetStatus(PipelinePatch patch)
        {
            var result = new PatchStatus { Patch = patch };

            var applied = 0;
            var problems = new List<string>();

            foreach (var edit in patch.Edits)
            {
                var path = ResolveTargetPath(edit);
                if (path == null || !File.Exists(path))
                {
                    result.State = PatchState.FileMissing;
                    result.Detail = path == null ? $"{PipelinePackageName} is not installed" : edit.RelativePath;
                    return result;
                }

                var source = Normalize(File.ReadAllText(path));
                if (source.Contains(patch.Marker))
                {
                    applied++;
                    continue;
                }

                var occurrences = CountOccurrences(source, Normalize(edit.Anchor));
                if (occurrences == 0)
                {
                    result.State = PatchState.AnchorNotFound;
                    result.Detail = $"{edit.RelativePath}: anchor not found — the package source differs from what this patch expects.";
                    return result;
                }

                if (occurrences > 1)
                {
                    result.State = PatchState.AnchorAmbiguous;
                    result.Detail = $"{edit.RelativePath}: anchor found {occurrences} times; refusing to guess where to apply.";
                    return result;
                }
            }

            if (applied == patch.Edits.Count)
                result.State = PatchState.Applied;
            else if (applied > 0)
            {
                result.State = PatchState.PartiallyApplied;
                result.Detail = $"{applied} of {patch.Edits.Count} files carry the marker. Revert to restore, then apply again.";
            }
            else
                result.State = PatchState.Applicable;

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

                foreach (var edit in patch.Edits)
                {
                    var path = ResolveTargetPath(edit);
                    var original = File.ReadAllText(path);

                    BackUp(path, original);

                    // Match on normalized text, then write back in whatever the file already used.
                    // Rewriting a package file's line endings wholesale would make every later diff
                    // against the package unreadable.
                    var usedCrLf = original.Contains("\r\n");
                    var patched = Normalize(original)
                        .Replace(Normalize(edit.Anchor), patch.Marker + "\n" + Normalize(edit.Replacement));

                    File.WriteAllText(path, usedCrLf ? patched.Replace("\n", "\r\n") : patched);
                }

                applied.Add(patch.Id);
            }

            RefreshDeferred();

            report = applied.Count == 0
                ? "Already applied; nothing to do."
                : "Applied: " + string.Join(", ", applied);
            return true;
        }

        /// <summary>
        /// Restore the backups taken at apply time.
        ///
        /// A file carrying our marker with no backup beside it cannot be reconstructed here — the
        /// original text is gone — so that case is reported rather than papered over with a guess.
        /// Reinstalling the package restores it.
        /// </summary>
        public static bool Revert(IEnumerable<PipelinePatch> patches, out string report)
        {
            var reverted = new List<string>();
            var failed = new List<string>();

            foreach (var patch in patches ?? Enumerable.Empty<PipelinePatch>())
            {
                var restored = 0;
                var problems = new List<string>();

                foreach (var edit in patch.Edits)
                {
                    var path = ResolveTargetPath(edit);
                    if (path == null || !File.Exists(path))
                    {
                        problems.Add($"{edit.RelativePath}: target file missing");
                        continue;
                    }

                    var backup = path + BackupSuffix;
                    if (File.Exists(backup))
                    {
                        File.Copy(backup, path, true);
                        File.Delete(backup);
                        restored++;
                        continue;
                    }

                    if (File.ReadAllText(path).Contains(patch.Marker))
                        problems.Add($"{edit.RelativePath}: backup missing — reinstall the package to restore the original");
                }

                if (restored > 0 && problems.Count == 0)
                    reverted.Add(patch.Id);
                else if (problems.Count > 0)
                    failed.Add($"{patch.Id}: " + string.Join("; ", problems));
                else
                    failed.Add($"{patch.Id}: not applied");
            }

            if (reverted.Count > 0)
                RefreshDeferred();

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

        /// <summary>
        /// Re-import on the next editor tick instead of inline.
        ///
        /// WHY: a caller driving this over HTTP is on a request that has to answer. Refreshing inline
        /// runs the import synchronously on the main thread, and on a project with a large asset in the
        /// queue that can exceed a minute — during which every other command fails with a main-thread
        /// timeout and nothing says why, because the request that caused it has not returned either.
        /// Deferring lets the apply report its result first; the reload then happens on its own.
        /// </summary>
        static void RefreshDeferred()
        {
            EditorApplication.delayCall += () => AssetDatabase.Refresh();
        }

        static void BackUp(string path, string originalSource)
        {
            var backup = path + BackupSuffix;

            // Never overwrite an existing backup: it holds the pristine text from the first apply, and
            // the current file may already carry an earlier patch of ours.
            if (!File.Exists(backup))
                File.WriteAllText(backup, originalSource);
        }

        static string ResolveTargetPath(PatchEdit edit)
        {
            var root = PackageRoot;
            if (root == null)
                return null;

            return Path.GetFullPath(Path.Combine(root, edit.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        /// <summary>
        /// Collapse line endings to LF so a multi-line anchor can match regardless of how either side
        /// was checked out.
        ///
        /// WHY this is not optional: the anchors live in C# verbatim strings, so they carry whatever
        /// endings this repository was cloned with, while the package source carries whatever Unity
        /// wrote into <c>PackageCache</c>. On a Windows checkout of this package against an LF package
        /// cache, every multi-line anchor mismatches on the first newline — which surfaced as every
        /// patch reporting AnchorNotFound, indistinguishable from the package genuinely having changed.
        /// </summary>
        static string Normalize(string text)
        {
            return text == null ? null : text.Replace("\r\n", "\n").Replace("\r", "\n");
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
