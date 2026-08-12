using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Reads/updates the in-editor confirmation settings behind PermissionGate
    /// (master switch + auto-reject timeout).
    /// </summary>
    [McpForUnityTool("manage_permissions", AutoRegister = false, Group = "core")]
    public static class ManagePermissions
    {
        public static object HandleCommand(JObject @params)
        {
            string action = @params?["action"]?.ToString()?.ToLowerInvariant() ?? "get";

            switch (action)
            {
                case "get":
                    return new SuccessResponse("Permission settings.", new
                    {
                        enabled = PermissionGate.Enabled,
                        timeout_s = PermissionGate.TimeoutSeconds,
                    });

                case "set_enabled":
                {
                    bool? enabled = @params?["enabled"]?.Value<bool>();
                    if (enabled == null)
                        return new ErrorResponse("'enabled' parameter (true/false) is required for set_enabled.");
                    PermissionGate.Enabled = enabled.Value;
                    return new SuccessResponse($"Confirmation {(enabled.Value ? "enabled" : "disabled")} for Confirm-level tools. Always-level tools still confirm.",
                        new { enabled = PermissionGate.Enabled, timeout_s = PermissionGate.TimeoutSeconds });
                }

                case "set_timeout":
                {
                    float? timeout = @params?["timeout_s"]?.Value<float>();
                    if (timeout == null)
                        return new ErrorResponse("'timeout_s' parameter (seconds) is required for set_timeout.");
                    PermissionGate.TimeoutSeconds = timeout.Value;
                    return new SuccessResponse($"Confirmation auto-reject timeout set to {PermissionGate.TimeoutSeconds:F0}s.",
                        new { enabled = PermissionGate.Enabled, timeout_s = PermissionGate.TimeoutSeconds });
                }

                default:
                    return new ErrorResponse($"Unknown action: '{action}'. Valid actions: get, set_enabled, set_timeout.");
            }
        }
    }
}
