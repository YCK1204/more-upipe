using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace MoreUpipe
{
    /// <summary>
    /// Reads the Editor console — the list a user sees in the Console window — through the internal
    /// <c>UnityEditor.LogEntries</c> API.
    ///
    /// WHY not the Pipeline's own console buffer: that buffer is internal, keeps the last 2000 entries
    /// only, and loses compile errors that were logged before it started. The Editor console holds
    /// everything since the last clear, compile errors included, and survives domain reloads.
    ///
    /// <c>LogEntries</c> honours the Console window's level toggles, so a user who hid warnings would
    /// hide them from every caller as well. Each read turns all three levels on and collapse off for
    /// the duration of the call and restores the user's flags before returning.
    /// </summary>
    internal static class EditorConsole
    {
        internal const string LevelError = "error";
        internal const string LevelWarn = "warn";
        internal const string LevelLog = "log";

        // LogMessageFlags bits, as Unity's C++ side defines them.
        const int kError = 1 << 0;
        const int kAssert = 1 << 1;
        const int kFatal = 1 << 4;
        const int kAssetImportError = 1 << 6;
        const int kAssetImportWarning = 1 << 7;
        const int kScriptingError = 1 << 8;
        const int kScriptingWarning = 1 << 9;
        const int kScriptCompileError = 1 << 11;
        const int kScriptCompileWarning = 1 << 12;
        const int kScriptingException = 1 << 17;
        const int kGraphCompileError = 1 << 20;
        const int kScriptingAssertion = 1 << 21;

        const int ErrorMask = kError | kAssert | kFatal | kAssetImportError | kScriptingError |
                              kScriptCompileError | kScriptingException | kGraphCompileError |
                              kScriptingAssertion;
        const int WarningMask = kAssetImportWarning | kScriptingWarning | kScriptCompileWarning;

        // ConsoleWindow.ConsoleFlags bits.
        const int FlagCollapse = 1 << 0;
        const int FlagLevels = (1 << 7) | (1 << 8) | (1 << 9);

        internal sealed class Entry
        {
            public int Index;
            public string Level;
            public bool Compile;
            public string Message;
            public string StackTrace;
            public string File;
            public int Line;
        }

        static readonly Type s_LogEntries;
        static readonly Type s_LogEntry;
        static readonly MethodInfo s_Start;
        static readonly MethodInfo s_End;
        static readonly MethodInfo s_GetEntry;
        static readonly MethodInfo s_GetCountsByType;
        static readonly PropertyInfo s_Flags;
        static readonly FieldInfo s_Message;
        static readonly FieldInfo s_Mode;
        static readonly FieldInfo s_File;
        static readonly FieldInfo s_Line;
        static readonly FieldInfo s_StackStart;

        static EditorConsole()
        {
            const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var editor = typeof(Editor).Assembly;
            s_LogEntries = editor.GetType("UnityEditor.LogEntries");
            s_LogEntry = editor.GetType("UnityEditor.LogEntry");
            if (s_LogEntries == null || s_LogEntry == null)
                return;

            s_Start = s_LogEntries.GetMethod("StartGettingEntries", statics);
            s_End = s_LogEntries.GetMethod("EndGettingEntries", statics);
            s_GetEntry = s_LogEntries.GetMethod("GetEntryInternal", statics);
            s_GetCountsByType = s_LogEntries.GetMethod("GetCountsByType", statics);
            s_Flags = s_LogEntries.GetProperty("consoleFlags", statics);
            s_Message = s_LogEntry.GetField("message", fields);
            s_Mode = s_LogEntry.GetField("mode", fields);
            s_File = s_LogEntry.GetField("file", fields);
            s_Line = s_LogEntry.GetField("line", fields);
            s_StackStart = s_LogEntry.GetField("callstackTextStartUTF16", fields);
        }

        /// <summary>Null when this Unity version exposes the console API in a shape this code knows.</summary>
        internal static string Unavailable =>
            s_Start == null || s_End == null || s_GetEntry == null || s_Flags == null ||
            s_Message == null || s_Mode == null
                ? "UnityEditor.LogEntries is not available in this Unity version."
                : null;

        /// <summary>Error, warning and log totals as the Console window counts them. Cheap: no entries are read.</summary>
        internal static (int errors, int warnings, int logs) Counts()
        {
            if (s_GetCountsByType == null)
                return (-1, -1, -1);

            var args = new object[] { 0, 0, 0 };
            s_GetCountsByType.Invoke(null, args);
            return ((int)args[0], (int)args[1], (int)args[2]);
        }

        /// <summary>
        /// Walk the console from the newest entry backwards, handing each one to <paramref name="visit"/>
        /// until it returns false or <paramref name="scan"/> entries have been read.
        /// </summary>
        /// <returns>Total number of entries in the console.</returns>
        internal static int ReadNewestFirst(int scan, Func<Entry, bool> visit)
        {
            var oldFlags = (int)s_Flags.GetValue(null);
            s_Flags.SetValue(null, (oldFlags | FlagLevels) & ~FlagCollapse);
            try
            {
                var count = (int)s_Start.Invoke(null, null);
                try
                {
                    var raw = Activator.CreateInstance(s_LogEntry, true);
                    var args = new object[2];
                    var stop = scan > 0 ? Math.Max(0, count - scan) : 0;

                    for (var i = count - 1; i >= stop; i--)
                    {
                        args[0] = i;
                        args[1] = raw;
                        s_GetEntry.Invoke(null, args);

                        if (!visit(ToEntry(i, raw)))
                            break;
                    }

                    return count;
                }
                finally
                {
                    s_End.Invoke(null, null);
                }
            }
            finally
            {
                s_Flags.SetValue(null, oldFlags);
            }
        }

        /// <summary>Rank of a level for "at least this severe" comparisons: log 0, warn 1, error 2.</summary>
        internal static int Severity(string level)
        {
            switch (level)
            {
                case LevelError: return 2;
                case LevelWarn: return 1;
                default: return 0;
            }
        }

        /// <summary>Parse a user-supplied level name, accepting the spellings Pipeline's console accepts.</summary>
        internal static string ParseLevel(string level)
        {
            switch ((level ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "error":
                case "errors":
                    return LevelError;
                case "warn":
                case "warning":
                case "warnings":
                    return LevelWarn;
                default:
                    return LevelLog;
            }
        }

        static Entry ToEntry(int index, object raw)
        {
            var mode = (int)s_Mode.GetValue(raw);
            var text = (string)s_Message.GetValue(raw) ?? string.Empty;

            // The stack starts where Unity says it does; older layouts without the field fall back to
            // the first line break, which is where a log message ends and its trace begins.
            var split = s_StackStart != null ? (int)s_StackStart.GetValue(raw) : -1;
            if (split <= 0 || split > text.Length)
                split = text.IndexOf('\n');
            if (split < 0)
                split = text.Length;

            return new Entry
            {
                Index = index,
                Level = (mode & ErrorMask) != 0 ? LevelError : (mode & WarningMask) != 0 ? LevelWarn : LevelLog,
                Compile = (mode & (kScriptCompileError | kScriptCompileWarning)) != 0,
                Message = text.Substring(0, split).TrimEnd(),
                StackTrace = split < text.Length ? text.Substring(split).Trim() : null,
                File = s_File != null ? (string)s_File.GetValue(raw) : null,
                Line = s_Line != null ? (int)s_Line.GetValue(raw) : 0,
            };
        }
    }
}
