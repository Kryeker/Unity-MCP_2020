"""Tests for the query_audit_log tool (pure file reader, no Unity round-trip)."""
import asyncio
import json
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from services.tools.query_audit_log import query_audit_log

LINES = [
    {"ts": "2026-08-12T09:00:00.000Z", "tool": "manage_asset", "type": "tool",
     "status": "SUCCESS", "ms": 12, "action": "get_info",
     "params": {"path": "Assets/X.prefab"}},
    {"ts": "2026-08-12T09:01:00.000Z", "tool": "execute_code", "type": "tool",
     "status": "ERROR", "ms": 300, "error": "boom"},
    {"ts": "2026-08-12T09:02:00.000Z", "tool": "scene_info", "type": "resource",
     "status": "SUCCESS", "ms": 1},
    {"ts": "2026-08-12T09:03:00.000Z", "tool": "manage_asset", "type": "tool",
     "status": "SUCCESS", "ms": 5, "action": "delete"},
    {"ts": "2026-08-12T10:00:00.000Z", "tool": "manage_asset", "type": "tool",
     "status": "ERROR", "ms": 7, "action": "delete"},
]

BROKEN_LINE = "this is { not json"

ERROR_LINES = [l for l in LINES if l["status"] == "ERROR"]


@pytest.fixture
def audit_dir(tmp_path: Path, monkeypatch) -> Path:
    """A fake project root with mcp.log and mcpError.log, no Unity instance."""
    log_dir = tmp_path / "Assets" / "UnityMCP" / "Log"
    log_dir.mkdir(parents=True)
    lines = [json.dumps(e, separators=(",", ":")) for e in LINES]
    lines.insert(3, BROKEN_LINE)
    (log_dir / "mcp.log").write_text("\n".join(lines) + "\n", encoding="utf-8")
    (log_dir / "mcpError.log").write_text(
        "\n".join(json.dumps(e, separators=(",", ":")) for e in ERROR_LINES) + "\n",
        encoding="utf-8",
    )
    monkeypatch.setattr(
        "services.tools.query_audit_log.get_unity_instance_from_context",
        AsyncMock(return_value=None),
    )
    return tmp_path


async def _query(project: Path, **kwargs):
    return await query_audit_log(SimpleNamespace(), project_path=str(project), **kwargs)


def test_newest_first_default(audit_dir):
    result = asyncio.run(_query(audit_dir))
    assert result["success"] is True
    entries = result["data"]["entries"]
    assert [e["ts"] for e in entries] == [l["ts"] for l in reversed(LINES)]
    assert result["data"]["total_matched"] == 5
    assert result["data"]["skipped_lines"] == 1  # the broken line
    assert result["data"]["by_status"] == {"SUCCESS": 3, "ERROR": 2}


def test_oldest_first(audit_dir):
    result = asyncio.run(_query(audit_dir, oldest_first=True))
    entries = result["data"]["entries"]
    assert entries[0]["ts"] == "2026-08-12T09:00:00.000Z"
    assert entries[-1]["ts"] == "2026-08-12T10:00:00.000Z"


def test_filter_tool(audit_dir):
    result = asyncio.run(_query(audit_dir, tool="manage_asset"))
    entries = result["data"]["entries"]
    assert len(entries) == 3
    assert all(e["tool"] == "manage_asset" for e in entries)


def test_filter_status(audit_dir):
    result = asyncio.run(_query(audit_dir, status="ERROR"))
    assert len(result["data"]["entries"]) == 2
    assert all(e["status"] == "ERROR" for e in result["data"]["entries"])


def test_filter_action(audit_dir):
    result = asyncio.run(_query(audit_dir, action="delete"))
    entries = result["data"]["entries"]
    assert len(entries) == 2
    assert all(e.get("action") == "delete" for e in entries)


def test_filter_record_type(audit_dir):
    result = asyncio.run(_query(audit_dir, record_type="resource"))
    entries = result["data"]["entries"]
    assert len(entries) == 1
    assert entries[0]["tool"] == "scene_info"


def test_filter_since_until(audit_dir):
    result = asyncio.run(_query(
        audit_dir,
        since="2026-08-12T09:01:00Z",
        until="2026-08-12T09:03:00.000Z",
    ))
    entries = result["data"]["entries"]
    # inclusive both ends, newest-first
    assert [e["ts"] for e in entries] == [
        "2026-08-12T09:03:00.000Z",
        "2026-08-12T09:02:00.000Z",
        "2026-08-12T09:01:00.000Z",
    ]


def test_filters_combine_with_and(audit_dir):
    result = asyncio.run(_query(audit_dir, tool="manage_asset", status="ERROR"))
    entries = result["data"]["entries"]
    assert len(entries) == 1
    assert entries[0]["action"] == "delete"


def test_limit_and_offset(audit_dir):
    result = asyncio.run(_query(audit_dir, limit=2, offset=1))
    entries = result["data"]["entries"]
    assert [e["ts"] for e in entries] == [
        "2026-08-12T09:03:00.000Z",
        "2026-08-12T09:02:00.000Z",
    ]


def test_error_only_reads_error_log(audit_dir):
    result = asyncio.run(_query(audit_dir, error_only=True))
    assert result["data"]["path"].endswith("mcpError.log")
    entries = result["data"]["entries"]
    assert len(entries) == 2
    assert all(e["status"] == "ERROR" for e in entries)


def test_missing_file_hints_at_enabling(tmp_path, monkeypatch):
    monkeypatch.setattr(
        "services.tools.query_audit_log.get_unity_instance_from_context",
        AsyncMock(return_value=None),
    )
    result = asyncio.run(_query(tmp_path))
    assert result["success"] is True
    assert result["data"]["exists"] is False
    assert result["data"]["entries"] == []
    assert "manage_audit" in result["message"]


def test_invalid_since_rejected(audit_dir):
    result = asyncio.run(_query(audit_dir, since="not-a-date"))
    assert result["success"] is False
    assert "since" in result["message"]


def test_invalid_until_rejected(audit_dir):
    result = asyncio.run(_query(audit_dir, until="not-a-date"))
    assert result["success"] is False
    assert "until" in result["message"]


def test_no_project_path(monkeypatch):
    monkeypatch.setattr(
        "services.tools.query_audit_log.get_unity_instance_from_context",
        AsyncMock(return_value="hash-1"),
    )
    monkeypatch.setattr(
        "services.tools.query_audit_log._resolve_project_path",
        AsyncMock(return_value=None),
    )
    result = asyncio.run(query_audit_log(SimpleNamespace()))
    assert result["success"] is False
    assert "project_path" in result["message"]


def test_limit_clamped(audit_dir):
    result = asyncio.run(_query(audit_dir, limit=0))
    assert result["success"] is True
    assert len(result["data"]["entries"]) == 1  # clamped to 1, newest only
