using System;
using System.Reflection;
using MCPForUnity.Editor.Services;
using UnityEditor;

namespace MCPForUnity.Editor.Helpers
{
    /// <summary>
    /// Unity compilation status checking and console error/warning counting.
    ///
    /// Error/warning counts are an approximation based on console log entries via the
    /// internal UnityEditor.LogEntries API. IMPORTANT: counts stay nullable — returning 0
    /// when counts are unknown is misleading (it reads as "validated: no errors/warnings").
    /// </summary>
    public static class CompilationHelper
    {
        // Last known compilation error/warning counts (from LogEntries). Kept in case the
        // reflection probe fails on a future Unity version — the last known value is still
        // better than a fabricated 0.
        private static int? _lastErrorCount;
        private static int? _lastWarningCount;

        // Reflection members for LogEntries.GetCountsByType(ref int errors, ref int warnings, ref int logs)
        private static MethodInfo _getCountsByTypeMethod;
        private static bool _reflectionProbed;

        /// <summary>
        /// True while the editor is compiling. Uses the event-tracked pipeline flag from
        /// EditorStateCache (works around isCompiling false positives, issues #549/#1276),
        /// falling back to the raw signals when the cache has not ticked yet.
        /// </summary>
        public static bool IsCompiling()
        {
            try
            {
                if (EditorStateCache.GetActualIsCompiling())
                    return true;
            }
            catch (Exception e)
            {
                McpLog.Warn($"[CompilationHelper] GetActualIsCompiling failed: {e.Message}");
            }
            return EditorApplication.isCompiling;
        }

        /// <summary>
        /// Number of error entries currently in the console, or null when unknown.
        /// Callers must treat null as "not validated" rather than "zero errors".
        /// </summary>
        public static int? GetCompilationErrors()
        {
            return GetCountsByType().Errors;
        }

        /// <summary>
        /// Number of warning entries currently in the console, or null when unknown.
        /// Callers must treat null as "not validated" rather than "zero warnings".
        /// </summary>
        public static int? GetCompilationWarnings()
        {
            return GetCountsByType().Warnings;
        }

        private static (int? Errors, int? Warnings) GetCountsByType()
        {
            var method = GetCountsByTypeMethod();
            if (method == null)
                return (_lastErrorCount, _lastWarningCount);

            try
            {
                // GetCountsByType(ref int errorCount, ref int warningCount, ref int logCount)
                var args = new object[] { 0, 0, 0 };
                method.Invoke(null, args);
                _lastErrorCount = (int)args[0];
                _lastWarningCount = (int)args[1];
                return (_lastErrorCount, _lastWarningCount);
            }
            catch (Exception e)
            {
                McpLog.Warn($"[CompilationHelper] GetCountsByType failed: {e.Message}");
                return (_lastErrorCount, _lastWarningCount);
            }
        }

        private static MethodInfo GetCountsByTypeMethod()
        {
            if (_reflectionProbed)
                return _getCountsByTypeMethod;

            _reflectionProbed = true;
            try
            {
                var logEntriesType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntries");
                if (logEntriesType == null)
                    return null;

                _getCountsByTypeMethod = logEntriesType.GetMethod(
                    "GetCountsByType",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch (Exception e)
            {
                McpLog.Warn($"[CompilationHelper] LogEntries reflection failed: {e.Message}");
            }
            return _getCountsByTypeMethod;
        }
    }
}
