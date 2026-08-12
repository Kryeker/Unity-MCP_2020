"""Tests for the manage_snapshots tool."""
import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from services.tools.manage_snapshots import manage_snapshots


@pytest.fixture
def mock_unity(monkeypatch):
    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["tool_name"] = tool_name
        captured["params"] = params
        return {"success": True, "message": "1 snapshot(s).",
                "data": {"snapshots": [{"id": "abc", "files": 2, "paths": ["Assets/A.mat"]}]}}

    monkeypatch.setattr(
        "services.tools.manage_snapshots.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr(
        "services.tools.manage_snapshots.send_with_unity_instance",
        fake_send,
    )
    return captured


def test_list_default(mock_unity):
    result = asyncio.run(manage_snapshots(SimpleNamespace()))
    assert result["success"] is True
    assert mock_unity["params"]["action"] == "list"


def test_rollback_forwards_id(mock_unity):
    asyncio.run(manage_snapshots(SimpleNamespace(), action="rollback", snapshot_id="abc123"))
    assert mock_unity["params"]["action"] == "rollback"
    assert mock_unity["params"]["snapshot_id"] == "abc123"


def test_delete_all_without_id(mock_unity):
    asyncio.run(manage_snapshots(SimpleNamespace(), action="delete"))
    assert mock_unity["params"]["action"] == "delete"
    assert "snapshot_id" not in mock_unity["params"]


def test_omits_unset_params(mock_unity):
    asyncio.run(manage_snapshots(SimpleNamespace(), action="list"))
    assert "snapshot_id" not in mock_unity["params"]
