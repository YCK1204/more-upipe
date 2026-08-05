using System;

namespace MoreUpipe.Patching
{
    /// <summary>
    /// One edit to a source file inside the installed <c>com.unity.pipeline</c> package.
    ///
    /// A patch is described as an anchor plus a replacement, not as a diff against a known file
    /// revision. The anchor is a distinctive snippet of the original source; the patch applies only
    /// when that snippet occurs exactly once. Zero occurrences means the package changed under us (or
    /// the patch is already applied); more than one means the anchor is not specific enough to place
    /// the edit safely. Both refuse rather than guess — a half-applied edit breaks compilation, and a
    /// broken Editor is a much worse outcome than a feature that did not install.
    /// </summary>
    public sealed class PipelinePatch
    {
        /// <summary>Stable identifier used by the UI and by the applied-state marker.</summary>
        public string Id { get; }

        /// <summary>Short human-readable summary shown in the patch window.</summary>
        public string Title { get; }

        /// <summary>What changes and why, shown as the description under the title.</summary>
        public string Description { get; }

        /// <summary>Path of the target file relative to the package root, using forward slashes.</summary>
        public string RelativePath { get; }

        /// <summary>Exact original snippet the edit attaches to. Must occur once.</summary>
        public string Anchor { get; }

        /// <summary>Text that takes the anchor's place. Usually contains the anchor itself.</summary>
        public string Replacement { get; }

        public PipelinePatch(string id, string title, string description, string relativePath, string anchor, string replacement)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("id is required", nameof(id));
            if (string.IsNullOrEmpty(relativePath)) throw new ArgumentException("relativePath is required", nameof(relativePath));
            if (string.IsNullOrEmpty(anchor)) throw new ArgumentException("anchor is required", nameof(anchor));

            Id = id;
            Title = title;
            Description = description;
            RelativePath = relativePath;
            Anchor = anchor;
            Replacement = replacement;
        }

        /// <summary>
        /// Marker written into the patched file. Presence of this string is the single source of truth
        /// for "is this patch applied?" — cheaper and more honest than hashing, because it survives
        /// unrelated edits to the same file and states plainly who wrote the change.
        /// </summary>
        public string Marker => "// [MoreUpipe:" + Id + "]";
    }
}
