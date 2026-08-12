using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Tools;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Helpers
{
    /// <summary>
    /// In-editor user approval for dangerous MCP commands.
    ///
    /// A command that requires confirmation shows a small non-modal dialog in the editor
    /// (approve / reject, with a countdown). The awaiting command stays pending — the
    /// bridge's async response channel keeps the client waiting — until the user answers
    /// or the timeout elapses, in which case the command is REJECTED (safe default).
    ///
    /// Non-modal rather than EditorUtility.DisplayDialog because the modal variant blocks
    /// the main thread, freezing the update tick that drives our async completions.
    ///
    /// Settings (EditorPrefs):
    ///   MCPForUnity.ConfirmationEnabled  – master switch for Confirm-level tools
    ///                                      (Always-level tools are never disabled).
    ///   MCPForUnity.ConfirmationTimeoutS – seconds before an unanswered dialog rejects
    ///                                      (default 60).
    /// </summary>
    [InitializeOnLoad]
    internal static class PermissionGate
    {
        private const string PrefsEnabled = "MCPForUnity.ConfirmationEnabled";
        private const string PrefsTimeout = "MCPForUnity.ConfirmationTimeoutS";
        private const float DefaultTimeoutSeconds = 60f;

        // One pending confirmation at a time; further requests queue behind it.
        private static readonly object _lock = new object();
        private static ConfirmDialog _activeDialog;

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefsEnabled, true);
            set => EditorPrefs.SetBool(PrefsEnabled, value);
        }

        public static float TimeoutSeconds
        {
            get => Mathf.Clamp(EditorPrefs.GetFloat(PrefsTimeout, DefaultTimeoutSeconds), 5f, 600f);
            set => EditorPrefs.SetFloat(PrefsTimeout, Mathf.Clamp(value, 5f, 600f));
        }

        /// <summary>
        /// True when a confirmation is required for this invocation (respecting the master
        /// switch, the action filter and the Always tier).
        /// </summary>
        public static bool NeedsConfirmation(ConfirmationLevel level, string actionFilter, string action)
        {
            if (level == ConfirmationLevel.None)
                return false;
            if (level == ConfirmationLevel.Confirm && !Enabled)
                return false;
            if (!string.IsNullOrEmpty(actionFilter)
                && !string.Equals(action, actionFilter, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        /// <summary>
        /// Requests user approval for a command. The returned task completes with true when
        /// the user approves, false on reject or timeout. Concurrent requests are serialized:
        /// a second request while one dialog is open is rejected immediately.
        /// </summary>
        public static async Task<bool> RequestConfirmation(
            string title, string message, string details, bool always)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ConfirmDialog dialog;

            lock (_lock)
            {
                if (_activeDialog != null)
                {
                    return false; // serialize: reject overlapping approvals
                }
                dialog = EditorWindow.GetWindow<ConfirmDialog>(true, title, false);
                dialog.Initialize(title, message, details, always, TimeoutSeconds, tcs);
                _activeDialog = dialog;
            }

            // Wall-clock watchdog on the thread pool: the update tick (and with it the
            // dialog's countdown) stalls when the editor window is unfocused, so an
            // unanswered dialog could otherwise hang the client forever.
            var cts = new System.Threading.CancellationTokenSource();
            Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds), cts.Token)
                .ContinueWith(t =>
                {
                    if (t.IsCanceled)
                        return;
                    lock (_lock)
                    {
                        if (_activeDialog == dialog)
                        {
                            McpLog.Warn("[PermissionGate] Confirmation timed out (wall clock); command rejected.");
                            dialog.ForceReject();
                        }
                    }
                }, TaskScheduler.Default);

            try
            {
                bool result = await tcs.Task.ConfigureAwait(true);
                return result;
            }
            finally
            {
                cts.Cancel();
                cts.Dispose();
                lock (_lock)
                {
                    if (_activeDialog == dialog)
                    {
                        _activeDialog = null;
                    }
                }
            }
        }

        /// <summary>
        /// Non-modal approval dialog: a small EditorWindow with allow/reject buttons and a
        /// countdown, driven by the editor update loop for the timeout. Closing the window
        /// without answering rejects.
        /// </summary>
        private sealed class ConfirmDialog : EditorWindow
        {
            private string _title;
            private string _message;
            private string _details;
            private bool _always;
            private double _deadline;
            private TaskCompletionSource<bool> _tcs;
            private bool _answered;

            public void Initialize(string title, string message, string details, bool always,
                float timeoutSeconds, TaskCompletionSource<bool> tcs)
            {
                _title = title;
                _message = message;
                _details = details;
                _always = always;
                _tcs = tcs;
                _deadline = EditorApplication.timeSinceStartup + timeoutSeconds;
                titleContent = new GUIContent(title);
                minSize = new Vector2(420, 200);
                Show();
                EditorApplication.update += Tick;
            }

            private void OnGUI()
            {
                GUILayout.Label(_message, EditorStyles.wordWrappedLabel);
                if (!string.IsNullOrEmpty(_details))
                {
                    EditorGUILayout.Space();
                    GUILayout.Label(_details, EditorStyles.helpBox);
                }
                EditorGUILayout.Space();

                double remaining = _deadline - EditorApplication.timeSinceStartup;
                GUILayout.Label($"{(remaining > 0 ? remaining.ToString("F0") : "0")}s to auto-reject",
                    EditorStyles.miniLabel);

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("允许 (Allow)", GUILayout.Height(30)))
                {
                    Answer(true);
                }
                if (GUILayout.Button("拒绝 (Reject)", GUILayout.Height(30)))
                {
                    Answer(false);
                }
                EditorGUILayout.EndHorizontal();
            }

            private void Tick()
            {
                if (_answered)
                    return;
                if (EditorApplication.timeSinceStartup >= _deadline)
                {
                    McpLog.Warn("[PermissionGate] Confirmation timed out; command rejected.");
                    Answer(false);
                }
            }

            private void Answer(bool approved)
            {
                if (_answered)
                    return;
                _answered = true;
                EditorApplication.update -= Tick;
                _tcs.TrySetResult(approved);
                Close();
            }

            /// <summary>Rejects without touching the main thread (watchdog path).</summary>
            public void ForceReject()
            {
                if (_answered)
                    return;
                _answered = true;
                EditorApplication.update -= Tick;
                _tcs.TrySetResult(false);
                // Close() must run on the main thread; the tick or the user closing the
                // window later handles it (guarded by _answered).
            }

            private void OnDestroy()
            {
                EditorApplication.update -= Tick;
                if (!_answered)
                {
                    _answered = true;
                    _tcs.TrySetResult(false); // window closed without answering = reject
                }
            }
        }
    }
}
