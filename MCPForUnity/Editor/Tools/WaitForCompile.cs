using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Waits for a compilation session to run to completion and reports its outcome
    /// (error/warning counts read from the console, valid across the reload).
    ///
    /// With <c>trigger=true</c> the tool first requests a script compilation
    /// (CompilationPipeline.RequestScriptCompilation), then waits for it — surviving the
    /// domain reload the successful compile triggers via the persisted
    /// <see cref="WaitForCompileSession"/>. With <c>trigger=false</c> it waits for whatever
    /// compilation is already in flight (or answers immediately when the editor is idle).
    ///
    /// Because our transport drops the connection during a domain reload, the Python client
    /// re-invokes this tool with the same <c>token</c> after reconnecting; the tool then
    /// returns the persisted outcome or re-attaches to the still-running session.
    /// </summary>
    [McpForUnityTool("wait_for_compile", AutoRegister = false, Group = "core")]
    public static class WaitForCompile
    {
        public const int DefaultTimeoutSeconds = 300;
        /// <summary>Detect window after a requested compile; Unity's start-up lag is long.</summary>
        private const double TriggeredSettleSeconds = 30;
        /// <summary>Detect window for a bare wait on an idle editor.</summary>
        private const double BareSettleSeconds = 2;

        public static async Task<object> HandleCommand(JObject @params)
        {
            var p = new ToolParams(@params ?? new JObject());
            int timeoutSeconds = p.GetInt("timeout_s") ?? DefaultTimeoutSeconds;
            if (timeoutSeconds <= 0)
                timeoutSeconds = DefaultTimeoutSeconds;
            bool trigger = p.GetBool("trigger", false);
            string token = p.Get("token") ?? Guid.NewGuid().ToString("N");

            // Re-invocation after a domain reload dropped the original connection: the
            // session finished in the meantime — hand back the persisted outcome.
            if (WaitSessionRunner.TryGetCompletedResult(token, out string completedJson))
                return Deserialize(completedJson);

            // A re-invocation (same token) may arrive while the original session is still in
            // flight; StartOrAttach reuses it. Only the first invocation triggers a compile —
            // CompileTriggered is persisted with the session, so a reconnect re-send never
            // requests a second compilation.
            var session = new WaitForCompileSession
            {
                Token = token,
                Deadline = EditorApplication.timeSinceStartup + timeoutSeconds,
                SettleSeconds = trigger ? TriggeredSettleSeconds : BareSettleSeconds,
            };

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            session = (WaitForCompileSession)WaitSessionRunner.StartOrAttach(session, tcs);

            if (trigger && !session.CompileTriggered)
            {
                session.CompileTriggered = true;
                try
                {
                    CompilationPipeline.RequestScriptCompilation();
                    WaitSessionRunner.Persist();
                }
                catch (Exception e)
                {
                    return new ErrorResponse($"Failed to request script compilation: {e.Message}");
                }
            }

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
