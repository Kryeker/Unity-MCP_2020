"""Tests for manage_asset dry_run parameter forwarding."""
import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from services.tools.manage_asset import manage_asset


@pytest.fixture
def mock_unity(monkeypatch):
    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, tool_name, params, **kwargs):
        captured["tool_name"] = tool_name
        captured["params"] = params
        return {"success": True, "message": "Dry run: 2 file(s) would be deleted.",
                "data": {"dryRun": True, "files": ["Assets/A.mat", "Assets/A.mat.meta"]}}

    monkeypatch.setattr(
        "services.tools.manage_asset.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr("services.tools.manage_asset.send_with_unity_instance", fake_send)
    return captured


def test_delete_dry_run_forwarded(mock_unity):
    asyncio.run(manage_asset(SimpleNamespace(), action="delete", path="Assets/A.mat", dry_run=True))
    assert mock_unity["params"]["action"] == "delete"
    assert mock_unity["params"]["dry_run"] is True


def test_delete_without_dry_run_omits_param(mock_unity):
    asyncio.run(manage_asset(SimpleNamespace(), action="delete", path="Assets/A.mat"))
    assert "dry_run" not in mock_unity["params"]
