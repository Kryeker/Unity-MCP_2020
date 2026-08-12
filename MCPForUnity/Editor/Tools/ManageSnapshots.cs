using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Manages file-level snapshots taken before destructive asset operations
    /// (delete/move via manage_asset). Rollback restores the asset bytes AND the
    /// sibling .meta, so GUIDs and references survive.
    /// </summary>
    [McpForUnityTool("manage_snapshots", AutoRegister = false, Group = "core")]
    public static class ManageSnapshots
    {
        public static object HandleCommand(JObject @params)
        {
            string action = @params?["action"]?.ToString()?.ToLowerInvariant() ?? "list";
            string snapshotId = @params?["snapshot_id"]?.ToString();

            switch (action)
            {
                case "list":
                    return SnapshotService.List();

                case "rollback":
                    if (string.IsNullOrEmpty(snapshotId))
                        return new ErrorResponse("'snapshot_id' is required for rollback.");
                    return SnapshotService.Rollback(snapshotId);

                case "delete":
                    return SnapshotService.Delete(snapshotId);

                default:
                    return new ErrorResponse(
                        $"Unknown action: '{action}'. Valid actions: list, rollback, delete.");
            }
        }
    }
}
