"""Tests for the Unity Insight adapter (unity_insight.py)."""
import asyncio
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

import pytest

from services.tools import unity_insight as ui


@pytest.fixture
def mock_unity(monkeypatch):
    captured: dict[str, object] = {"args": None, "project_path": None}

    async def fake_run(args, project_path=None, timeout_s=ui._TIMEOUT_SECONDS):
        captured["args"] = args
        captured["project_path"] = project_path
        return {"success": True, "stdout": "", "stderr": ""}

    monkeypatch.setattr(
        "services.tools.unity_insight.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr("services.tools.unity_insight._run_insight", fake_run)
    monkeypatch.setattr(
        "services.tools.unity_insight._get_project_path",
        AsyncMock(return_value=r"C:\Projects\Demo"),
    )
    return captured


# --- CLI discovery ---

def test_find_insight_missing_raises(monkeypatch):
    monkeypatch.delenv("UNITY_INSIGHT_CLI", raising=False)
    monkeypatch.delenv("UNITY_INSIGHT_BUNDLE", raising=False)
    monkeypatch.setattr(ui, "_candidate_roots", lambda: [Path("/nonexistent")])
    with pytest.raises(ui.InsightUnavailableError):
        ui._find_insight()


def test_is_available_false_when_missing(monkeypatch):
    monkeypatch.setattr(
        ui, "_find_insight",
        lambda: (_ for _ in ()).throw(ui.InsightUnavailableError("missing")),
    )
    assert ui._is_available() is False


def test_unavailable_error_surfaces(monkeypatch):
    async def fake_run(args, project_path=None, timeout_s=ui._TIMEOUT_SECONDS):
        return {"success": False, "error": "unity-insight-cli not found."}

    monkeypatch.setattr(
        "services.tools.unity_insight.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr("services.tools.unity_insight._run_insight", fake_run)
    result = asyncio.run(ui.unity_refs(SimpleNamespace(), path="Assets/A.mat", direction="in"))
    assert result["success"] is False
    assert "not found" in result["message"]


# --- output parsing ---

def test_parse_refs_output_groups_files():
    out = (
        "Found 2 incoming VFS references for 'Assets/A.mat'\n"
        "\n"
        "Assets/Scenes/Main.unity\n"
        "  - /Main/Root/Cube/MeshRenderer.comp\n"
        "Assets/Prefabs/Cube.prefab\n"
        "  - /Cube/MeshRenderer.comp\n"
    )
    data = ui._parse_refs_output(out)
    assert data["summary"].startswith("Found 2")
    assert len(data["groups"]) == 2
    assert data["groups"][0]["file"] == "Assets/Scenes/Main.unity"
    assert data["groups"][0]["nodes"] == ["/Main/Root/Cube/MeshRenderer.comp"]


def test_parse_ls_output_table():
    out = (
        "Listed 2 entries under 'Assets' (depth=1)\n"
        "\n"
        "SIZE_KB  PATH\n"
        "\n"
        "       4  - Assets/Materials/\n"
        "       2  - Assets/Scenes/\n"
    )
    data = ui._parse_ls_output(out)
    assert len(data["entries"]) == 2
    assert data["entries"][0]["path"] == "Assets/Materials/"
    assert data["entries"][0]["size_kb"] == 4


# --- tool parameter construction ---

def test_unity_refs_forwards_params(mock_unity):
    asyncio.run(ui.unity_refs(
        SimpleNamespace(), path="Assets/Materials/Red.mat", direction="in"))
    assert mock_unity["args"] == ["vfs_refs", "--path", "Assets/Materials/Red.mat", "--direction", "in"]
    assert mock_unity["project_path"] == r"C:\Projects\Demo"


def test_unity_refs_target_type(mock_unity):
    asyncio.run(ui.unity_refs(
        SimpleNamespace(), path="Assets/A.prefab", direction="out", target_type="Scene"))
    assert "--target_type" in mock_unity["args"]
    assert mock_unity["args"][-1] == "Scene"


def test_unity_search_forwards_params(mock_unity):
    asyncio.run(ui.unity_search(
        SimpleNamespace(), pattern="**/*Door*.prefab", type="Prefab", ignore_case=False, limit=50))
    assert mock_unity["args"][0] == "vfs_glob"
    assert "--ignore_case" in mock_unity["args"]
    assert mock_unity["args"][mock_unity["args"].index("--limit") + 1] == "50"


def test_unity_ls_depth_clamped(mock_unity):
    asyncio.run(ui.unity_ls(SimpleNamespace(), path="Assets", depth=99))
    args = mock_unity["args"]
    assert args[args.index("--depth") + 1] == "5"


def test_unity_read_forwards_params(mock_unity):
    asyncio.run(ui.unity_read(
        SimpleNamespace(), path="Assets/Scripts/Foo.cs:/.content"))
    assert mock_unity["args"][0] == "vfs_read"


def test_unity_index_status(mock_unity):
    asyncio.run(ui.unity_index(SimpleNamespace(), action="status"))
    assert mock_unity["args"][0] == "index"
    assert mock_unity["args"][1] == "status"


# --- stdout passthrough for read ---

def test_unity_read_returns_content(monkeypatch):
    async def fake_run(args, project_path=None, timeout_s=ui._TIMEOUT_SECONDS):
        return {"success": True, "stdout": "guid: abc123\nMonoImporter:", "stderr": ""}

    monkeypatch.setattr(
        "services.tools.unity_insight.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr("services.tools.unity_insight._run_insight", fake_run)
    result = asyncio.run(ui.unity_read(SimpleNamespace(), path="Assets/Foo.mat"))
    assert result["success"] is True
    assert "abc123" in result["data"]["content"]


# --- output encoding sniffing ---

def test_decode_utf8_output():
    raw = "Found 1 for 'Assets/穴位.mat'".encode("utf-8")
    assert ui._decode_cli_output(raw) == "Found 1 for 'Assets/穴位.mat'"


def test_decode_gbk_output():
    raw = "Found 1 for 'Assets/穴位.mat'".encode("gbk")
    decoded = ui._decode_cli_output(raw)
    assert "穴位" in decoded


def test_decode_lossy_fallback():
    raw = b"ok \xff\xfe garbage"
    assert "garbage" in ui._decode_cli_output(raw)
