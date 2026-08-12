"""
Status/control for the structured MCP audit log (JSONL written by the Unity editor
to Assets/UnityMCP/Log/mcp.log). Recording is off by default; enable it here or via
the "Log Record" toggle in the MCP Advanced window. Use query_audit_log to read
and filter the recorded entries.
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
        "Query or update the structured MCP audit log recording setting.\n\n"
        "The editor records one JSON line per tool/resource call (ts, tool, type, status, "
        "ms, action, params, error) to Assets/UnityMCP/Log/mcp.log. Recording is off by "
        "default.\n\n"
        "Actions:\n"
        "- get_status: whether recording is enabled, log file paths, sizes and last-write times\n"
        "- set_enabled: turn recording on (true) or off (false)\n\n"
        "Use query_audit_log to read and filter the recorded entries."
    ),
    group="core",
    annotations=ToolAnnotations(
        title="Manage Audit Log",
        destructiveHint=False,
    ),
)
async def manage_audit(
    ctx: Context,
    action: Annotated[
        Literal["get_status", "set_enabled"],
        "Action to perform.",
    ] = "get_status",
    enabled: Annotated[
        Optional[bool],
        "For set_enabled: true to record all tool/resource calls, false to stop.",
    ] = None,
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)

    params: dict[str, Any] = {"action": action}
    if enabled is not None:
        params["enabled"] = bool(enabled)

    response = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "manage_audit",
        params,
    )

    if not isinstance(response, dict):
        response = response.model_dump() if hasattr(response, "model_dump") else response.__dict__

    return {
        "success": response.get("success", False),
        "message": response.get("message", response.get("error", "")),
        "data": response.get("data"),
    }
