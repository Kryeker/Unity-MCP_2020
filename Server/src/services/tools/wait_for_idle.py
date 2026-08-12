"""
Wait for the Unity editor to become idle.

Blocks until the editor reports idle — no in-flight bridge jobs (test runs, package
installs, execute_code coroutines), not compiling, not importing assets — then returns
once with the elapsed wait and the final state.

Idempotent across domain reloads: each call carries an internal token; if a reload drops
the connection mid-wait, the transport layer reconnects and re-invokes with the same
token, and Unity returns the persisted outcome (or re-attaches to the still-running wait).
"""
from __future__ import annotations

import uuid
from typing import Annotated, Any

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


@mcp_for_unity_tool(
    description=(
        "Wait until the Unity editor is idle: no in-flight bridge jobs (test runs, package "
        "installs, execute_code coroutines), not compiling, not importing assets. "
        "Returns once, with the elapsed wait and the final state. "
        "Survives domain reloads automatically. Default timeout 600s."
    ),
    group="core",
    annotations=ToolAnnotations(
        title="Wait For Idle",
        destructiveHint=False,
        readOnlyHint=True,
    ),
)
async def wait_for_idle(
    ctx: Context,
    timeout_s: Annotated[
        int,
        "Maximum seconds to wait for the editor to become idle. Default: 600.",
    ] = 600,
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)

    # Idempotency key: survives a reconnect re-send after a domain reload so Unity can
    # return the persisted outcome of the original wait instead of starting a new one.
    token = uuid.uuid4().hex
    params = {"timeout_s": int(timeout_s), "token": token}

    # Budget = C#-side timeout + transport margin (reconnect/retry during domain reload).
    response = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "wait_for_idle",
        params,
        total_timeout=float(timeout_s) + 60.0,
    )

    if not isinstance(response, dict):
        response = response.model_dump() if hasattr(response, "model_dump") else response.__dict__

    return {
        "success": response.get("success", False),
        "message": response.get("message", response.get("error", "")),
        "data": response.get("data"),
    }
