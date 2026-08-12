"""Tests for the manage_permissions tool."""
import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from services.tools.manage_permissions import manage_permissions


@pytest.fixture
def mock_unity(monkeypatch):
    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["tool_name"] = tool_name
        captured["params"] = params
        return {"success": True, "message": "Permission settings.",
                "data": {"enabled": True, "timeout_s": 60.0}}

    monkeypatch.setattr(
        "services.tools.manage_permissions.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr(
        "services.tools.manage_permissions.send_with_unity_instance",
        fake_send,
    )
    return captured


def test_get_defaults(mock_unity):
    result = asyncio.run(manage_permissions(SimpleNamespace()))
    assert result["success"] is True
    assert mock_unity["params"]["action"] == "get"


def test_set_enabled_forwards(mock_unity):
    asyncio.run(manage_permissions(SimpleNamespace(), action="set_enabled", enabled=False))
    assert mock_unity["params"]["action"] == "set_enabled"
    assert mock_unity["params"]["enabled"] is False


def test_set_timeout_forwards(mock_unity):
    asyncio.run(manage_permissions(SimpleNamespace(), action="set_timeout", timeout_s=30))
    assert mock_unity["params"]["timeout_s"] == 30.0


def test_omits_unset_params(mock_unity):
    asyncio.run(manage_permissions(SimpleNamespace(), action="get"))
    assert "enabled" not in mock_unity["params"]
    assert "timeout_s" not in mock_unity["params"]
