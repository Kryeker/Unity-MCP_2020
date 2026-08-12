"""
Wait for a Unity script compilation to run to completion and report its outcome.

With trigger=true the tool first requests a script compilation (which triggers a domain
reload on success — the wait survives it), then reports error/warning counts read from the
console. With trigger=false it waits for whatever compilation is already in flight, or
answers immediately when the editor is idle.

Idempotent across domain reloads: each call carries an internal token; if a reload drops
the connection mid-wait, the transport layer reconnects and re-invokes with the same
token, and Unity returns the persisted outcome (or re-attaches to the still-running wait).
The compile is requested exactly once per token.
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
        "Wait for a Unity script compilation to finish and report its outcome. "
        "trigger=true first requests a compilation (CompilationPipeline.RequestScriptCompilation) "
        "and waits for it, surviving the domain reload it triggers. trigger=false waits for an "
        "in-flight compilation, or answers immediately when the editor is idle. "
        "Result carries compile_observed (did a compile actually start), hasErrors/hasWarnings "
        "and error/warning counts; when compile_observed is false the counts belong to the "
        "previous build — validate via console. Default timeout 300s."
    ),
    group="core",
    annotations=ToolAnnotations(
        title="Wait For Compile",
        destructiveHint=False,
    ),
)
async def wait_for_compile(
    ctx: Context,
    trigger: Annotated[
        bool,
        "Request a script compilation before waiting. Only the first invocation with a "
        "given token triggers; re-invocations after a reconnect never re-trigger. "
        "Default: false.",
    ] = False,
    timeout_s: Annotated[
        int,
        "Maximum seconds to wait for the compilation session to finish. Default: 300.",
    ] = 300,
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)

    # Idempotency key: survives a reconnect re-send after a domain reload so Unity can
    # return the persisted outcome of the original wait instead of starting a new one.
    token = uuid.uuid4().hex
    params = {"timeout_s": int(timeout_s), "trigger": bool(trigger), "token": token}

    # Budget = C#-side timeout + transport margin (reconnect/retry during domain reload).
    response = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "wait_for_compile",
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
