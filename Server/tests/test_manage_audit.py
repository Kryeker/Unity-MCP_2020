"""Tests for the manage_audit tool (audit log recording switch)."""
import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from services.tools.manage_audit import manage_audit


@pytest.fixture
def mock_unity(monkeypatch):
    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["tool_name"] = tool_name
        captured["params"] = params
        return {"success": True, "message": "Audit log status.",
                "data": {"enabled": True, "log_exists": False, "log_size": 0}}

    monkeypatch.setattr(
        "services.tools.manage_audit.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr(
        "services.tools.manage_audit.send_with_unity_instance",
        fake_send,
    )
    return captured


def test_get_status_default(mock_unity):
    result = asyncio.run(manage_audit(SimpleNamespace()))
    assert result["success"] is True
    assert mock_unity["params"]["action"] == "get_status"
    assert mock_unity["tool_name"] == "manage_audit"


def test_set_enabled_forwards(mock_unity):
    asyncio.run(manage_audit(SimpleNamespace(), action="set_enabled", enabled=True))
    assert mock_unity["params"]["action"] == "set_enabled"
    assert mock_unity["params"]["enabled"] is True


def test_omits_unset_params(mock_unity):
    asyncio.run(manage_audit(SimpleNamespace(), action="set_enabled"))
    assert "enabled" not in mock_unity["params"]
