"""Tests for wait_for_compile tool."""
import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from services.tools.wait_for_compile import wait_for_compile


@pytest.fixture
def mock_unity(monkeypatch):
    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, tool_name, params, **kwargs):
        captured["tool_name"] = tool_name
        captured["params"] = params
        captured["total_timeout"] = kwargs.get("total_timeout")
        return {
            "success": True,
            "message": "Compilation completed successfully",
            "data": {
                "status": "completed",
                "compile_observed": True,
                "hasErrors": False,
                "errors": 0,
                "warnings": 2,
                "elapsed_ms": 15400.0,
            },
        }

    monkeypatch.setattr(
        "services.tools.wait_for_compile.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr(
        "services.tools.wait_for_compile.send_with_unity_instance",
        fake_send,
    )
    return captured


def test_forwards_tool_name_and_params(mock_unity):
    result = asyncio.run(wait_for_compile(SimpleNamespace(), trigger=True, timeout_s=180))
    assert result["success"] is True
    assert mock_unity["tool_name"] == "wait_for_compile"
    assert mock_unity["params"]["timeout_s"] == 180
    assert mock_unity["params"]["trigger"] is True
    assert mock_unity["params"]["token"]


def test_trigger_defaults_false(mock_unity):
    asyncio.run(wait_for_compile(SimpleNamespace()))
    assert mock_unity["params"]["trigger"] is False


def test_default_timeout_is_300(mock_unity):
    asyncio.run(wait_for_compile(SimpleNamespace()))
    assert mock_unity["params"]["timeout_s"] == 300


def test_total_timeout_budget_covers_wait_plus_margin(mock_unity):
    asyncio.run(wait_for_compile(SimpleNamespace(), timeout_s=300))
    assert mock_unity["total_timeout"] == 360.0


def test_token_unique_per_call(mock_unity):
    asyncio.run(wait_for_compile(SimpleNamespace()))
    first = mock_unity["params"]["token"]
    asyncio.run(wait_for_compile(SimpleNamespace()))
    assert mock_unity["params"]["token"] != first


def test_returns_compile_outcome(mock_unity):
    result = asyncio.run(wait_for_compile(SimpleNamespace()))
    data = result["data"]
    assert data["compile_observed"] is True
    assert data["hasErrors"] is False
    assert data["errors"] == 0


def test_error_response_surface(monkeypatch):
    async def fake_send(send_fn, unity_instance, tool_name, params, **kwargs):
        return {"success": False, "error": "Timed out waiting for compilation (phase 'await')."}

    monkeypatch.setattr(
        "services.tools.wait_for_compile.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr(
        "services.tools.wait_for_compile.send_with_unity_instance",
        fake_send,
    )

    result = asyncio.run(wait_for_compile(SimpleNamespace(), timeout_s=10))
    assert result["success"] is False
    assert "Timed out" in result["message"]
