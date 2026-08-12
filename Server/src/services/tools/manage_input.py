"""
Simulate mouse/keyboard input in the Unity editor during PlayMode.

Dispatches across multiple editor frames (press/release separated by a frame,
drags interpolated). UI elements are driven through the EventSystem (reliable for
uGUI); gameplay input outside UI goes through OS-level SendInput and needs the
Unity window focused. Text entry targets the focused uGUI/TMP input field via the
IMGUI event queue.
"""
from __future__ import annotations

from typing import Annotated, Any, Literal, Optional

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry

VALID_ACTIONS = {
    "mouse_click", "mouse_move", "mouse_drag", "mouse_scroll",
    "mouse_down", "mouse_up", "key_press", "key_down", "key_up", "type_text",
}


@mcp_for_unity_tool(
    description=(
        "Simulate mouse and keyboard input in the Unity editor (requires PlayMode). "
        "Coordinates are Unity screen space, bottom-left origin.\n\n"
        "Mouse actions (x, y, button: 0=left 1=right 2=middle):\n"
        "- mouse_click: click at (x, y)\n"
        "- mouse_move: move cursor / update UI hover\n"
        "- mouse_drag: drag from (from_x, from_y) to (x, y) at drag_speed px/s\n"
        "- mouse_scroll: scroll at (x, y) with scroll_x/scroll_y\n"
        "- mouse_down / mouse_up: press and hold / release a button\n\n"
        "Keyboard actions:\n"
        "- key_press / key_down / key_up: key names like 'W', 'Space', 'Enter', 'LeftShift'\n"
        "- type_text: type text into the focused uGUI/TMP input field (select it first "
        "with mouse_click)\n\n"
        "target param (optional): 'auto' (default, UI when hit else device), 'ui' (force "
        "EventSystem path, error if nothing hit), 'world'/'device' (force device path). "
        "The device path uses OS-level SendInput and requires the Unity window to be "
        "focused during PlayMode."
    ),
    group="core",
    annotations=ToolAnnotations(
        title="Manage Input",
        destructiveHint=False,
    ),
)
async def manage_input(
    ctx: Context,
    action: Annotated[
        Literal[
            "mouse_click", "mouse_move", "mouse_drag", "mouse_scroll",
            "mouse_down", "mouse_up", "key_press", "key_down", "key_up", "type_text",
        ],
        "Input action to simulate.",
    ],
    x: Annotated[Optional[float], "Screen X (bottom-left origin) for mouse actions."] = None,
    y: Annotated[Optional[float], "Screen Y (bottom-left origin) for mouse actions."] = None,
    from_x: Annotated[Optional[float], "Drag start X."] = None,
    from_y: Annotated[Optional[float], "Drag start Y."] = None,
    button: Annotated[Optional[int], "Mouse button: 0=left, 1=right, 2=middle."] = None,
    drag_speed: Annotated[Optional[float], "Drag speed in px/s (default 500, 0=instant)."] = None,
    scroll_x: Annotated[Optional[float], "Horizontal scroll delta."] = None,
    scroll_y: Annotated[Optional[float], "Vertical scroll delta."] = None,
    key: Annotated[Optional[str], "Key name for keyboard actions (e.g. 'W', 'Space', 'Enter')."] = None,
    text: Annotated[Optional[str], "Text to type into the focused input field."] = None,
    target: Annotated[
        Optional[Literal["auto", "ui", "world", "device"]],
        "Path selection: auto (default), ui (force EventSystem), world/device (force device).",
    ] = None,
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)

    if action not in VALID_ACTIONS:
        return {
            "success": False,
            "message": f"Unknown action '{action}'. Valid actions: {', '.join(sorted(VALID_ACTIONS))}",
        }

    params: dict[str, Any] = {"action": action}
    if x is not None:
        params["x"] = x
    if y is not None:
        params["y"] = y
    if from_x is not None:
        params["from_x"] = from_x
    if from_y is not None:
        params["from_y"] = from_y
    if button is not None:
        params["button"] = button
    if drag_speed is not None:
        params["drag_speed"] = drag_speed
    if scroll_x is not None:
        params["scroll_x"] = scroll_x
    if scroll_y is not None:
        params["scroll_y"] = scroll_y
    if key is not None:
        params["key"] = key
    if text is not None:
        params["text"] = text
    if target is not None:
        params["target"] = target

    response = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "manage_input",
        params,
    )

    if not isinstance(response, dict):
        response = response.model_dump() if hasattr(response, "model_dump") else response.__dict__

    return {
        "success": response.get("success", False),
        "message": response.get("message", response.get("error", "")),
        "data": response.get("data"),
    }
