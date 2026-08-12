using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Waits until the editor is idle — no in-flight bridge jobs (test runs, package
    /// installs, execute_code coroutines), not compiling, not importing assets — and answers
    /// once, on the original request.
    ///
    /// The wait survives domain reloads: the session is persisted to SessionState and resumes
    /// after the reload (see <see cref="WaitSessionRunner"/>). Because our transport drops the
    /// connection during a reload, the Python client re-invokes this tool with the same
    /// <c>token</c> after reconnecting; the tool then returns the persisted outcome or
    /// re-attaches to the still-running session. Behavior mirrors Tuanjie's wait_for_idle
    /// (JobRegistry + compilation/import checks, 600s default timeout).
    /// </summary>
    [McpForUnityTool("wait_for_idle", AutoRegister = false, Group = "core")]
    public static class WaitForIdle
    {
        public const int DefaultTimeoutSeconds = 600;

        public static async Task<object> HandleCommand(JObject @params)
        {
            var p = new ToolParams(@params ?? new JObject());
            int timeoutSeconds = p.GetInt("timeout_s") ?? DefaultTimeoutSeconds;
            if (timeoutSeconds <= 0)
                timeoutSeconds = DefaultTimeoutSeconds;
            string token = p.Get("token") ?? Guid.NewGuid().ToString("N");

            // Re-invocation after a domain reload dropped the original connection and the
            // session finished in the meantime: hand back the persisted outcome.
            if (WaitSessionRunner.TryGetCompletedResult(token, out string completedJson))
                return Deserialize(completedJson);

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            WaitSessionRunner.StartOrAttach(new WaitForIdleSession
            {
                Token = token,
                Deadline = EditorApplication.timeSinceStartup + timeoutSeconds,
            }, tcs);

            string json = await tcs.Task.ConfigureAwait(true);
            return Deserialize(json);
        }

        private static object Deserialize(string json)
        {
            try { return JObject.Parse(json); }
            catch { return new ErrorResponse("Failed to parse wait result."); }
        }
    }
}
