"""Tests for the manage_input tool."""
import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from services.tools.manage_input import manage_input


@pytest.fixture
def mock_unity(monkeypatch):
    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["tool_name"] = tool_name
        captured["params"] = params
        return {"success": True, "message": "Clicked UI element 'Button' at (100.0, 200.0)",
                "data": {"hitGameObject": "Button", "method": "EventSystem"}}

    monkeypatch.setattr(
        "services.tools.manage_input.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr(
        "services.tools.manage_input.send_with_unity_instance",
        fake_send,
    )
    return captured


def test_click_forwards_params(mock_unity):
    result = asyncio.run(manage_input(
        SimpleNamespace(), action="mouse_click", x=100, y=200))
    assert result["success"] is True
    assert mock_unity["tool_name"] == "manage_input"
    assert mock_unity["params"]["action"] == "mouse_click"
    assert mock_unity["params"]["x"] == 100
    assert mock_unity["params"]["y"] == 200


def test_drag_forwards_params(mock_unity):
    asyncio.run(manage_input(
        SimpleNamespace(), action="mouse_drag",
        from_x=10, from_y=20, x=300, y=20, drag_speed=400))
    p = mock_unity["params"]
    assert p["action"] == "mouse_drag"
    assert p["from_x"] == 10
    assert p["drag_speed"] == 400


def test_key_press_forwards_key(mock_unity):
    asyncio.run(manage_input(SimpleNamespace(), action="key_press", key="Space"))
    assert mock_unity["params"]["key"] == "Space"


def test_type_text_forwards_text(mock_unity):
    asyncio.run(manage_input(SimpleNamespace(), action="type_text", text="hello"))
    assert mock_unity["params"]["text"] == "hello"


def test_omits_unset_params(mock_unity):
    asyncio.run(manage_input(SimpleNamespace(), action="mouse_click", x=5, y=5))
    p = mock_unity["params"]
    assert "key" not in p
    assert "text" not in p
    assert "drag_speed" not in p


def test_unknown_action_rejected(mock_unity):
    result = asyncio.run(manage_input(SimpleNamespace(), action="fly"))
    assert result["success"] is False
    assert "fly" in result["message"]
    assert "tool_name" not in mock_unity  # not forwarded


def test_target_param_forwarded(mock_unity):
    asyncio.run(manage_input(
        SimpleNamespace(), action="mouse_click", x=1, y=2, target="ui"))
    assert mock_unity["params"]["target"] == "ui"
