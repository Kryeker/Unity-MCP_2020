"""
Query the structured MCP audit log (JSONL) written by the Unity editor.

The editor writes one JSON object per line to <project>/Assets/UnityMCP/Log/mcp.log
for every tool/resource call (ts, tool, type, status, ms, action, params, error),
and mirrors ERROR entries to mcpError.log. Recording is off by default -- enable it
with manage_audit set_enabled or the "Log Record" toggle in the MCP Advanced window.

This tool reads the log file directly (no Unity round-trip); entries are returned
newest-first by default.
"""
from __future__ import annotations

import json
from datetime import datetime
from pathlib import Path
from typing import Annotated, Any, Optional

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context

_LOG_SUBDIR = Path("Assets") / "UnityMCP" / "Log"
_LOG_FILENAME = "mcp.log"
_ERROR_LOG_FILENAME = "mcpError.log"
_DEFAULT_LIMIT = 100
_MAX_LIMIT = 1000


def _parse_ts(value: str) -> datetime | None:
    """Parse an ISO-8601 timestamp from the log (always UTC, 'Z' suffix)."""
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None


def _matches(entry: dict[str, Any], filters: dict[str, Any]) -> bool:
    for key, wanted in filters.items():
        if wanted is None:
            continue
        actual = entry.get(key)
        if key == "tool":
            # 'tool' field also carries resource names (type=resource).
            if actual != wanted:
                return False
        elif key in ("since", "until"):
            ts = _parse_ts(entry.get("ts", ""))
            if ts is None:
                continue  # unparseable ts never filters an entry out
            if key == "since" and ts < filters["since"]:
                return False
            if key == "until" and ts > filters["until"]:
                return False
        elif key == "record_type":
            if entry.get("type") != wanted:
                return False
        elif actual != wanted:
            return False
    return True


async def _resolve_project_path(unity_instance: str | None) -> str | None:
    """Project root for the current Unity instance (via the plugin hub registry)."""
    if not unity_instance:
        return None
    try:
        from transport.plugin_hub import PluginHub

        registry = PluginHub._registry
        if not registry:
            return None
        target_hash = unity_instance
        if "@" in target_hash:
            _, _, target_hash = target_hash.rpartition("@")
        if not target_hash:
            return None
        session_id = await registry.get_session_id_by_hash(target_hash)
        if not session_id:
            return None
        session = await registry.get_session(session_id)
        if not session:
            return None
        return session.project_path or session.project_name
    except Exception:  # noqa: BLE001
        return None


def _query_file(
    log_path: Path,
    tool: str | None,
    record_type: str | None,
    status: str | None,
    action: str | None,
    since: datetime | None,
    until: datetime | None,
    limit: int,
    offset: int,
    oldest_first: bool,
) -> tuple[list[dict[str, Any]], int, int]:
    """Read and filter one JSONL audit file. Returns (entries, total_matched, skipped_lines)."""
    skipped = 0
    entries: list[dict[str, Any]] = []
    if not log_path.exists():
        return entries, 0, 0

    filters: dict[str, Any] = {
        "tool": tool,
        "record_type": record_type,
        "status": status,
        "action": action,
        "since": since,
        "until": until,
    }

    for line in log_path.read_text(encoding="utf-8", errors="replace").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            entry = json.loads(line)
        except json.JSONDecodeError:
            skipped += 1
            continue
        if not isinstance(entry, dict):
            skipped += 1
            continue
        if _matches(entry, filters):
            entries.append(entry)

    total = len(entries)
    if not oldest_first:
        entries.reverse()
    paged = entries[offset:offset + limit]
    return paged, total, skipped


@mcp_for_unity_tool(
    description=(
        "Query the structured MCP audit log (JSONL) recorded by the Unity editor for every "
        "tool/resource call: ts, tool, type (tool/resource/unknown), status, ms, action, params, error.\n\n"
        "Reads <project>/Assets/UnityMCP/Log/mcp.log directly (no Unity round-trip). Recording is "
        "off by default -- enable it with manage_audit set_enabled=true or the 'Log Record' toggle "
        "in the MCP Advanced window; get_status via manage_audit reports whether recording is on.\n\n"
        "Filters combine with AND. Entries are returned newest-first; use offset for paging."
    ),
    group="core",
    annotations=ToolAnnotations(
        title="Query Audit Log",
        readOnlyHint=True,
        destructiveHint=False,
    ),
)
async def query_audit_log(
    ctx: Context,
    project_path: Annotated[
        Optional[str],
        "Project root override (default: resolved from the connected Unity instance).",
    ] = None,
    tool: Annotated[
        Optional[str],
        "Filter by exact tool or resource name (e.g. manage_asset).",
    ] = None,
    record_type: Annotated[
        Optional[str],
        "Filter by entry type: tool, resource or unknown.",
    ] = None,
    status: Annotated[
        Optional[str],
        "Filter by result status (e.g. SUCCESS, ERROR).",
    ] = None,
    action: Annotated[
        Optional[str],
        "Filter by the action parameter value (e.g. delete).",
    ] = None,
    since: Annotated[
        Optional[str],
        "Only entries at or after this time (ISO-8601, e.g. 2026-08-12T10:00:00Z).",
    ] = None,
    until: Annotated[
        Optional[str],
        "Only entries at or before this time (ISO-8601).",
    ] = None,
    error_only: Annotated[
        bool,
        "Query mcpError.log (ERROR entries only) instead of mcp.log.",
    ] = False,
    limit: Annotated[int, "Max entries to return (1-1000, default 100)."] = _DEFAULT_LIMIT,
    offset: Annotated[int, "Skip this many matching entries (for paging)."] = 0,
    oldest_first: Annotated[
        bool,
        "Return entries oldest-first (default: newest-first).",
    ] = False,
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)
    root = project_path or await _resolve_project_path(unity_instance)
    if not root:
        return {
            "success": False,
            "message": (
                "Could not resolve the Unity project path. Pass project_path explicitly "
                "or connect a Unity instance first."
            ),
            "data": None,
        }

    limit = max(1, min(int(limit), _MAX_LIMIT))
    offset = max(0, int(offset))

    since_dt = _parse_ts(since) if since else None
    until_dt = _parse_ts(until) if until else None
    if since and since_dt is None:
        return {"success": False, "message": f"Invalid 'since' timestamp: {since!r}", "data": None}
    if until and until_dt is None:
        return {"success": False, "message": f"Invalid 'until' timestamp: {until!r}", "data": None}

    log_path = Path(root) / _LOG_SUBDIR / (_ERROR_LOG_FILENAME if error_only else _LOG_FILENAME)
    entries, total, skipped = _query_file(
        log_path, tool, record_type, status, action, since_dt, until_dt, limit, offset, oldest_first,
    )

    by_status: dict[str, int] = {}
    for entry in entries:
        s = entry.get("status") or "UNKNOWN"
        by_status[s] = by_status.get(s, 0) + 1

    data: dict[str, Any] = {
        "path": str(log_path),
        "exists": log_path.exists(),
        "count": len(entries),
        "total_matched": total,
        "offset": offset,
        "skipped_lines": skipped,
        "by_status": by_status,
        "entries": entries,
    }

    if not log_path.exists():
        return {
            "success": True,
            "message": (
                f"No audit log at {log_path}. Recording is off by default -- enable it with "
                "manage_audit set_enabled=true (or the 'Log Record' toggle in the MCP Advanced window)."
            ),
            "data": data,
        }

    return {
        "success": True,
        "message": f"{len(entries)} of {total} matching entries (skipped {skipped} unparseable lines).",
        "data": data,
    }
