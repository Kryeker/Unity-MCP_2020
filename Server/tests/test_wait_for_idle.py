"""Tests for wait_for_idle tool."""
import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from services.tools.wait_for_idle import wait_for_idle


@pytest.fixture
def mock_unity(monkeypatch):
    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, tool_name, params, **kwargs):
        captured["tool_name"] = tool_name
        captured["params"] = params
        captured["total_timeout"] = kwargs.get("total_timeout")
        return {"success": True, "message": "Editor is idle.", "data": {"elapsed_ms": 123.4}}

    monkeypatch.setattr(
        "services.tools.wait_for_idle.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr(
        "services.tools.wait_for_idle.send_with_unity_instance",
        fake_send,
    )
    return captured


def test_forwards_tool_name_and_timeout(mock_unity):
    result = asyncio.run(wait_for_idle(SimpleNamespace(), timeout_s=120))
    assert result["success"] is True
    assert mock_unity["tool_name"] == "wait_for_idle"
    assert mock_unity["params"]["timeout_s"] == 120
    assert mock_unity["params"]["token"]


def test_default_timeout_is_600(mock_unity):
    asyncio.run(wait_for_idle(SimpleNamespace()))
    assert mock_unity["params"]["timeout_s"] == 600


def test_total_timeout_budget_covers_wait_plus_margin(mock_unity):
    asyncio.run(wait_for_idle(SimpleNamespace(), timeout_s=300))
    assert mock_unity["total_timeout"] == 360.0


def test_token_unique_per_call(mock_unity):
    asyncio.run(wait_for_idle(SimpleNamespace()))
    first = mock_unity["params"]["token"]
    asyncio.run(wait_for_idle(SimpleNamespace()))
    assert mock_unity["params"]["token"] != first


def test_returns_parsed_response(mock_unity):
    result = asyncio.run(wait_for_idle(SimpleNamespace()))
    assert result["data"]["elapsed_ms"] == 123.4


def test_error_response_surface(monkeypatch):
    async def fake_send(send_fn, unity_instance, tool_name, params, **kwargs):
        return {"success": False, "error": "Timed out waiting for the editor to become idle."}

    monkeypatch.setattr(
        "services.tools.wait_for_idle.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr(
        "services.tools.wait_for_idle.send_with_unity_instance",
        fake_send,
    )

    result = asyncio.run(wait_for_idle(SimpleNamespace(), timeout_s=10))
    assert result["success"] is False
    assert "Timed out" in result["message"]
