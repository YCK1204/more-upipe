using System;
using System.Collections.Generic;

namespace MoreUpipe.Patching
{
    /// <summary>
    /// A single file edit: the snippet to find and the text that takes its place.
    /// </summary>
    public sealed class PatchEdit
    {
        /// <summary>Path of the target file relative to the package root, using forward slashes.</summary>
        public string RelativePath { get; }

        /// <summary>Exact original snippet the edit attaches to. Must occur once.</summary>
        public string Anchor { get; }

        /// <summary>Text that takes the anchor's place. Usually contains the anchor itself.</summary>
        public string Replacement { get; }

        public PatchEdit(string relativePath, string anchor, string replacement)
        {
            if (string.IsNullOrEmpty(relativePath)) throw new ArgumentException("relativePath is required", nameof(relativePath));
            if (string.IsNullOrEmpty(anchor)) throw new ArgumentException("anchor is required", nameof(anchor));

            RelativePath = relativePath;
            Anchor = anchor;
            Replacement = replacement;
        }
    }

    /// <summary>
    /// One change to the installed <c>com.unity.pipeline</c> package, expressed as one or more file
    /// edits that are applied together.
    ///
    /// A patch is described as anchors plus replacements, not as a diff against a known file revision.
    /// An anchor is a distinctive snippet of the original source; an edit applies only when that
    /// snippet occurs exactly once. Zero occurrences means the package changed under us (or the patch
    /// is already applied); more than one means the anchor is not specific enough to place the edit
    /// safely. Both refuse rather than guess — a half-applied edit breaks compilation, and a broken
    /// Editor is a far worse outcome than a feature that did not install.
    ///
    /// WHY a patch carries several edits instead of shipping them as separate entries: a change that
    /// spans a command and its response model only compiles when both land. Grouping them makes
    /// "apply one but not the other" unrepresentable rather than merely discouraged.
    /// </summary>
    public sealed class PipelinePatch
    {
        /// <summary>Stable identifier used by the UI and by the applied-state marker.</summary>
        public string Id { get; }

        /// <summary>Short human-readable summary shown in the patch window.</summary>
        public string Title { get; }

        /// <summary>What changes and why, shown as the description under the title.</summary>
        public string Description { get; }

        /// <summary>Edits belonging to this patch. All apply, or none do.</summary>
        public IReadOnlyList<PatchEdit> Edits { get; }

        public PipelinePatch(string id, string title, string description, params PatchEdit[] edits)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("id is required", nameof(id));
            if (edits == null || edits.Length == 0) throw new ArgumentException("at least one edit is required", nameof(edits));

            Id = id;
            Title = title;
            Description = description;
            Edits = edits;
        }

        /// <summary>
        /// Marker written into each patched file. Presence of this string is the single source of truth
        /// for "is this patch applied?" — cheaper and more honest than hashing, because it survives
        /// unrelated edits to the same file and states plainly who wrote the change.
        /// </summary>
        public string Marker => "// [MoreUpipe:" + Id + "]";

        /// <summary>Files this patch touches, for display.</summary>
        public string TargetSummary => string.Join(", ", System.Linq.Enumerable.Select(Edits, edit => edit.RelativePath));
    }
}
