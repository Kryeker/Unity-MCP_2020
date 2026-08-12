"""
Manage file-level snapshots taken before destructive asset operations.

manage_asset delete/move automatically captures a snapshot (asset bytes + .meta)
before executing; rollback restores them so GUIDs and references survive.
"""
from __future__ import annotations

from typing import Annotated, Any, Literal, Optional

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


@mcp_for_unity_tool(
    description=(
        "Manage file-level snapshots taken before destructive asset operations "
        "(manage_asset delete/move). Snapshots bundle the asset bytes with the sibling "
        ".meta so rollback never orphans a GUID.\n\n"
        "Actions:\n"
        "- list: show snapshots (id, age, file count, paths)\n"
        "- rollback: restore a snapshot's files and refresh the asset database "
        "(requires snapshot_id)\n"
        "- delete: remove a snapshot (or all when snapshot_id is omitted)\n\n"
        "Snapshots expire after 24h and are kept under Library/McpSnapshots."
    ),
    group="core",
    annotations=ToolAnnotations(
        title="Manage Snapshots",
        destructiveHint=False,
    ),
)
async def manage_snapshots(
    ctx: Context,
    action: Annotated[Literal["list", "rollback", "delete"], "Action to perform."] = "list",
    snapshot_id: Annotated[
        Optional[str],
        "Snapshot id for rollback/delete (omit for delete-all).",
    ] = None,
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)

    params: dict[str, Any] = {"action": action}
    if snapshot_id is not None:
        params["snapshot_id"] = snapshot_id

    response = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "manage_snapshots",
        params,
    )

    if not isinstance(response, dict):
        response = response.model_dump() if hasattr(response, "model_dump") else response.__dict__

    return {
        "success": response.get("success", False),
        "message": response.get("message", response.get("error", "")),
        "data": response.get("data"),
    }
