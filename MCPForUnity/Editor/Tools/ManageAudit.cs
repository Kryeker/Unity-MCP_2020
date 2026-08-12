using System.IO;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Status/control for the structured MCP audit log (JSONL written by McpLogRecord
    /// to Assets/UnityMCP/Log/mcp.log). Use query_audit_log to read and filter entries.
    /// </summary>
    [McpForUnityTool("manage_audit", AutoRegister = false, Group = "core")]
    public static class ManageAudit
    {
        public static object HandleCommand(JObject @params)
        {
            string action = @params?["action"]?.ToString()?.ToLowerInvariant() ?? "get_status";

            switch (action)
            {
                case "get_status":
                case "status":
                {
                    var logInfo = new FileInfo(McpLogRecord.LogFilePath);
                    var errorInfo = new FileInfo(McpLogRecord.ErrorLogFilePath);
                    return new SuccessResponse("Audit log status.", new
                    {
                        enabled = McpLogRecord.IsEnabled,
                        log_path = McpLogRecord.LogFilePath,
                        error_path = McpLogRecord.ErrorLogFilePath,
                        log_exists = logInfo.Exists,
                        log_size = logInfo.Exists ? logInfo.Length : 0,
                        log_modified = logInfo.Exists
                            ? logInfo.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                            : null,
                        error_exists = errorInfo.Exists,
                        error_size = errorInfo.Exists ? errorInfo.Length : 0,
                    });
                }

                case "set_enabled":
                {
                    bool? enabled = @params?["enabled"]?.Value<bool>();
                    if (enabled == null)
                        return new ErrorResponse("'enabled' parameter (true/false) is required for set_enabled.");
                    McpLogRecord.IsEnabled = enabled.Value;
                    return new SuccessResponse($"Audit log recording {(enabled.Value ? "enabled" : "disabled")}.",
                        new { enabled = McpLogRecord.IsEnabled });
                }

                default:
                    return new ErrorResponse($"Unknown action: '{action}'. Valid actions: get_status, set_enabled.");
            }
        }
    }
}
