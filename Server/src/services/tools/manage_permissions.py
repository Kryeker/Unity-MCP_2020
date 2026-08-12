"""
Query/update the in-editor permission settings (confirmation prompts for dangerous
commands). Confirmation prompts appear in the Unity editor; unanswered dialogs
auto-reject after the timeout.
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
        "Query or update the in-editor confirmation settings for dangerous MCP commands.\n\n"
        "Dangerous commands (e.g. execute_code, asset deletion, package removal) show an "
        "approval dialog in the Unity editor before running; unanswered dialogs auto-reject "
        "after the timeout.\n\n"
        "Actions:\n"
        "- get: current settings (enabled, timeout_s)\n"
        "- set_enabled: master switch for Confirm-level tools (Always-level tools like "
        "execute_code still confirm regardless)\n"
        "- set_timeout: auto-reject timeout in seconds (5-600)"
    ),
    group="core",
    annotations=ToolAnnotations(
        title="Manage Permissions",
        destructiveHint=False,
    ),
)
async def manage_permissions(
    ctx: Context,
    action: Annotated[Literal["get", "set_enabled", "set_timeout"], "Action to perform."] = "get",
    enabled: Annotated[
        Optional[bool],
        "For set_enabled: true to require confirmation for Confirm-level tools, false to skip.",
    ] = None,
    timeout_s: Annotated[
        Optional[float],
        "For set_timeout: auto-reject timeout in seconds (5-600).",
    ] = None,
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)

    params: dict[str, Any] = {"action": action}
    if enabled is not None:
        params["enabled"] = bool(enabled)
    if timeout_s is not None:
        params["timeout_s"] = float(timeout_s)

    response = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "manage_permissions",
        params,
    )

    if not isinstance(response, dict):
        response = response.model_dump() if hasattr(response, "model_dump") else response.__dict__

    return {
        "success": response.get("success", False),
        "message": response.get("message", response.get("error", "")),
        "data": response.get("data"),
    }
