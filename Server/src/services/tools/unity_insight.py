"""
Unity Insight adapter.

Wraps the installed Tuanjie unity-insight-cli (read-only Unity project analysis:
asset reference graph, typed search, scene/prefab hierarchy, script bindings).

The CLI ships inside Tuanjie Cowork (closed source, per-project SQLite index under
<project>/Library/UnityInsight). This module only spawns the external program —
nothing from Tuanjie is bundled here. Discovery order:

1. UNITY_INSIGHT_CLI env var (path to unity-insight-cli.cmd, or to a
   node.exe + bundle directory pair)
2. Default Tuanjie Cowork install locations on Windows
3. UNITY_INSIGHT_BUNDLE env var (path to the bundle dir, when CLI discovery fails)

Design: one short-lived process per query (node cold start + SQLite read is <1s).
Index maintenance goes through the same CLI (index status / sync / build).
"""
from __future__ import annotations

import asyncio
import logging
import os
import shutil
from pathlib import Path
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context

logger = logging.getLogger(__name__)

_INSIGHT_REL = Path("cli/bin/win32-x64")
_BUNDLE_REL = Path("cli/bin/win32-x64/lib/bundle")

_TIMEOUT_SECONDS = 300  # index build on a large project can take minutes


class InsightUnavailableError(RuntimeError):
    """Raised when the unity-insight-cli binary cannot be located."""


def _candidate_roots() -> list[Path]:
    roots: list[Path] = []
    env = os.environ.get("TUANJIE_COWORK_DIR")
    if env:
        roots.append(Path(env))
    if os.name == "nt":
        local = os.environ.get("LOCALAPPDATA")
        if local:
            roots.append(Path(local) / "Programs" / "Tuanjie Cowork")
        roots.append(Path("C:/Program Files/Tuanjie Cowork"))
    # WSL view of the Windows user profile (when the server runs inside WSL).
    wsl_profile = Path("/mnt/c/Users")
    if wsl_profile.exists():
        for user in wsl_profile.iterdir():
            roots.append(user / "AppData/Local/Programs/Tuanjie Cowork")
    return roots


def _find_insight() -> tuple[Path, Path]:
    """Returns (node_executable, bundle_js) for unity-insight-cli.

    Raises InsightUnavailableError when not found.
    """
    env_cli = os.environ.get("UNITY_INSIGHT_CLI")
    if env_cli:
        p = Path(env_cli)
        if p.exists():
            # A path to the .cmd wrapper: derive node + bundle from it.
            base = p.parent  # cli/bin/win32-x64
            node = base / "node.exe"
            bundle = base / "lib" / "bundle" / "unity-insight-cli.js"
            if node.exists() and bundle.exists():
                return node, bundle
            return p, Path()  # fall through to subprocess via the wrapper

    env_bundle = os.environ.get("UNITY_INSIGHT_BUNDLE")
    if env_bundle:
        b = Path(env_bundle)
        bundle = b / "unity-insight-cli.js"
        if bundle.exists():
            return _find_node_near(b), bundle

    for root in _candidate_roots():
        node = root / _INSIGHT_REL / "node.exe"
        bundle = root / _BUNDLE_REL / "unity-insight-cli.js"
        if node.exists() and bundle.exists():
            return node, bundle

    raise InsightUnavailableError(
        "unity-insight-cli not found. Install Tuanjie Cowork, or set "
        "UNITY_INSIGHT_CLI / UNITY_INSIGHT_BUNDLE."
    )


def _find_node_near(bundle_dir: Path) -> Path:
    """Locate a node executable next to the bundle (bundled layout) or on PATH."""
    for candidate in [
        bundle_dir.parent.parent / "node.exe",  # .../cli/bin/win32-x64/node.exe
        bundle_dir.parent / "node.exe",
    ]:
        if candidate.exists():
            return candidate
    node = shutil.which("node")
    if node:
        return Path(node)
    raise InsightUnavailableError("node.exe not found next to unity-insight-cli bundle.")


def _is_available() -> bool:
    try:
        _find_insight()
        return True
    except InsightUnavailableError:
        return False


async def _get_project_path(unity_instance: str | None) -> str | None:
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
    except Exception as e:  # noqa: BLE001
        if isinstance(e, asyncio.CancelledError):
            raise
        logger.debug("Could not resolve project path: %s", e)
        return None


async def _run_insight(
    args: list[str],
    project_path: str | None = None,
    timeout_s: float = _TIMEOUT_SECONDS,
) -> dict[str, Any]:
    """Run unity-insight-cli once. Returns {success, stdout, stderr, command}."""
    try:
        node, bundle = _find_insight()
    except InsightUnavailableError as e:
        return {"success": False, "error": str(e)}

    cmd: list[str]
    if bundle.exists():
        cmd = [str(node), str(bundle), *args]
    else:
        cmd = [str(Path(os.environ.get("UNITY_INSIGHT_CLI"))), *args]

    if project_path:
        cmd += ["--project", project_path]

    logger.debug("unity-insight: %s", " ".join(str(c) for c in cmd))
    try:
        proc = await asyncio.create_subprocess_exec(
            *cmd,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
        )
        stdout, stderr = await asyncio.wait_for(proc.communicate(), timeout=timeout_s)
    except asyncio.TimeoutError:
        return {
            "success": False,
            "error": f"unity-insight timed out after {timeout_s}s.",
            "command": " ".join(str(c) for c in cmd),
        }
    except OSError as e:
        return {"success": False, "error": f"Failed to run unity-insight: {e}"}

    out = _decode_cli_output(stdout)
    err = _decode_cli_output(stderr)
    # node's SQLite experimental warning is noise, not an error.
    err_lines = [ln for ln in err.splitlines()
                 if "ExperimentalWarning" not in ln and "trace-warnings" not in ln]

    if proc.returncode != 0:
        detail = "\n".join(err_lines[-10:]) if err_lines else f"exit code {proc.returncode}"
        return {
            "success": False,
            "error": f"unity-insight failed ({proc.returncode}): {detail}",
            "stdout": out[-4000:],
        }
    return {"success": True, "stdout": out, "stderr": "\n".join(err_lines)}


def _decode_cli_output(raw: bytes) -> str:
    """Decode CLI output; the node-based CLI can emit UTF-8 or GBK depending on
    its console-code-page detection, so sniff: strict UTF-8 first (rejecting
    obvious mojibake), then GBK, then lossy UTF-8."""
    for enc in ("utf-8", "gbk"):
        try:
            s = raw.decode(enc)
        except UnicodeDecodeError:
            continue
        if enc == "utf-8" and _looks_like_gbk_mojibake(s):
            continue
        return s
    return raw.decode("utf-8", errors="replace")


def _looks_like_gbk_mojibake(s: str) -> bool:
    """GBK bytes misread as UTF-8 surface as runs of Cyrillic/Greek letters
    (GBK lead bytes 0xD0-0xD7 decode as Cyrillic, 0xA8-0xBF as Greek)."""
    suspicious = sum(
        1 for ch in s
        if "\u0400" <= ch <= "\u04FF" or "\u03B0" <= ch <= "\u03FF"
    )
    return suspicious > 1


def _parse_ls_output(stdout: str) -> dict[str, Any]:
    """Parse 'Matched/Listed N ...' + SIZE_KB PATH table output into entries."""
    entries: list[dict[str, Any]] = []
    for line in stdout.splitlines():
        stripped = line.strip()
        if not stripped or stripped.startswith("SIZE_KB") or stripped.startswith("-"):
            if stripped.startswith("- "):
                path = stripped[2:].strip()
                if path:
                    entries.append({"path": path})
            continue
        # 'SIZE_KB  PATH' table rows: '     18  - Assets/...'
        parts = stripped.split("  ", 1)
        if len(parts) == 2 and parts[0].strip().isdigit():
            path = parts[1].strip()
            if path.startswith("- "):
                path = path[2:].strip()
            entries.append({"path": path, "size_kb": int(parts[0].strip())})
    return {"entries": entries}


def _parse_refs_output(stdout: str) -> dict[str, Any]:
    """Parse refs output: 'Found N ...' header + grouped file paths with - /node rows."""
    groups: list[dict[str, Any]] = []
    current_file: str | None = None
    nodes: list[str] = []
    header = stdout.splitlines()[0] if stdout.splitlines() else ""

    for line in stdout.splitlines()[1:]:
        stripped = line.strip()
        if not stripped:
            continue
        if stripped.startswith("- "):
            nodes.append(stripped[2:].strip())
        else:
            if current_file is not None:
                groups.append({"file": current_file, "nodes": nodes})
                nodes = []
            current_file = stripped

    if current_file is not None:
        groups.append({"file": current_file, "nodes": nodes})

    return {"summary": header, "groups": groups}


async def _insight_call(
    ctx: Context,
    subcommand: str,
    params: list[str],
    project_path: str | None = None,
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)
    if project_path is None:
        project_path = await _get_project_path(unity_instance)
    result = await _run_insight([subcommand, *params], project_path)
    return result


@mcp_for_unity_tool(
    description=(
        "Query the Unity project reference graph (Unity Insight): which assets reference "
        "a given asset, what an asset references, and script bindings. "
        "Read-only, powered by an external per-project index.\n\n"
        "Path forms (behavior inferred from path):\n"
        "- 'Assets/.../Foo.cs' -> script bindings: which prefabs/scenes mount this script "
        "(always incoming)\n"
        "- 'Assets/.../Foo.mat' / '.prefab' / '.unity' / model -> asset references\n"
        "- 'Assets/.../Foo.cs:/Foo/Bar' -> C# call references (method level)\n\n"
        "direction: 'in' = who references this path (e.g. which scenes use this material); "
        "'out' = what this path references (default). target_type narrows returned nodes "
        "(e.g. Scene, Prefab, Material, Component, GameObject)."
    ),
    group="core",
    annotations=ToolAnnotations(
        title="Unity Insight Refs",
        readOnlyHint=True,
        destructiveHint=False,
    ),
)
async def unity_refs(
    ctx: Context,
    path: Annotated[
        str,
        "VFS path to query, e.g. 'Assets/Materials/Red.mat' or 'Assets/Scripts/Foo.cs'. "
        "Use ':/' for file-internal nodes: 'Assets/A.prefab:/Root'.",
    ],
    direction: Annotated[
        Literal["in", "out"],
        "in = who references this path; out = what this path references (default). "
        "Ignored for .cs script paths (always incoming bindings).",
    ] = "out",
    target_type: Annotated[
        str | None,
        "Narrow returned nodes by type (Scene, Prefab, Material, Texture, Mesh, Model, "
        "AnimationClip, AnimatorController, Component, GameObject, Script, ALL).",
    ] = None,
) -> dict[str, Any]:
    params = ["--path", path, "--direction", direction]
    if target_type and target_type.upper() != "ALL":
        params += ["--target_type", target_type]

    result = await _insight_call(ctx, "vfs_refs", params)
    if not result.get("success"):
        return {"success": False, "message": result.get("error", "unknown error"),
                "data": {"stdout": result.get("stdout", "")}}
    data = _parse_refs_output(result["stdout"])
    message = data["summary"] or "Query completed."
    return {"success": True, "message": message, "data": data}


@mcp_for_unity_tool(
    description=(
        "Find Unity assets/nodes whose VFS paths match a glob pattern, with type filtering "
        "(Scene, Prefab, Script, Material, Texture, Mesh, Model, AnimationClip, "
        "AnimatorController, GameObject, Component, ...). Read-only, powered by the Unity "
        "Insight index. Use for locating assets by name before unity_refs."
    ),
    group="core",
    annotations=ToolAnnotations(
        title="Unity Insight Search",
        readOnlyHint=True,
        destructiveHint=False,
    ),
)
async def unity_search(
    ctx: Context,
    pattern: Annotated[
        str,
        "Glob pattern over VFS paths, e.g. '**/*Door*.prefab' or 'Assets/UI/**'.",
    ],
    type: Annotated[
        str,
        "Type filter for matched entries (Scene, Prefab, Script, Material, Texture, Mesh, "
        "Model, AnimationClip, AnimatorController, GameObject, Component, ... or ALL). "
        "Prefer concrete types.",
    ] = "ALL",
    ignore_case: Annotated[bool, "Case-insensitive matching (default true)."] = True,
    limit: Annotated[int, "Maximum matches (1-500, default 100)."] = 100,
) -> dict[str, Any]:
    params = [
        "--pattern", pattern,
        "--type", type,
        "--ignore_case", "true" if ignore_case else "false",
        "--limit", str(max(1, min(limit, 500))),
    ]
    result = await _insight_call(ctx, "vfs_glob", params)
    if not result.get("success"):
        return {"success": False, "message": result.get("error", "unknown error"),
                "data": {"stdout": result.get("stdout", "")}}
    data = _parse_ls_output(result["stdout"])
    data["summary"] = result["stdout"].splitlines()[0] if result["stdout"].splitlines() else ""
    return {"success": True, "message": data["summary"], "data": data}


@mcp_for_unity_tool(
    description=(
        "List the Unity Insight virtual file system under a path: project folders, asset "
        "hierarchy (prefab/scene GameObject tree), or script structure. Read-only. "
        "Suffix paths with '/' for directories; use ':/' for file-internal nodes."
    ),
    group="core",
    annotations=ToolAnnotations(
        title="Unity Insight List",
        readOnlyHint=True,
        destructiveHint=False,
    ),
)
async def unity_ls(
    ctx: Context,
    path: Annotated[str, "VFS path to list, e.g. 'Assets' or 'Assets/A.prefab'."],
    depth: Annotated[int, "Recursion depth (1-5, default 1)."] = 1,
) -> dict[str, Any]:
    params = ["--path", path, "--depth", str(max(1, min(depth, 5)))]
    result = await _insight_call(ctx, "vfs_ls", params)
    if not result.get("success"):
        return {"success": False, "message": result.get("error", "unknown error"),
                "data": {"stdout": result.get("stdout", "")}}
    data = _parse_ls_output(result["stdout"])
    data["summary"] = result["stdout"].splitlines()[0] if result["stdout"].splitlines() else ""
    return {"success": True, "message": data["summary"], "data": data}


@mcp_for_unity_tool(
    description=(
        "Read a Unity Insight VFS entry: asset .meta (guid/importer), an indexed node's "
        "serialized content, or full text via 'file:/.content'. Read-only."
    ),
    group="core",
    annotations=ToolAnnotations(
        title="Unity Insight Read",
        readOnlyHint=True,
        destructiveHint=False,
    ),
)
async def unity_read(
    ctx: Context,
    path: Annotated[
        str,
        "VFS path, e.g. 'Assets/Foo.mat' (.meta), 'Assets/A.prefab:/Root/Node' (node), "
        "or 'Assets/Scripts/Foo.cs:/.content' (full text).",
    ],
    depth: Annotated[int, "Recursion depth for node reads (0-5, default 0)."] = 0,
) -> dict[str, Any]:
    params = ["--path", path, "--depth", str(max(0, min(depth, 5)))]
    result = await _insight_call(ctx, "vfs_read", params)
    if not result.get("success"):
        return {"success": False, "message": result.get("error", "unknown error"),
                "data": {"stdout": result.get("stdout", "")}}
    return {"success": True, "message": "Read completed.", "data": {"content": result["stdout"]}}


@mcp_for_unity_tool(
    description=(
        "Manage the Unity Insight index for the current project: status (index freshness, "
        "file counts), sync (incremental update), build (full rebuild, slow). Read-only "
        "status; sync/build write only the index under Library/UnityInsight."
    ),
    group="core",
    annotations=ToolAnnotations(
        title="Unity Insight Index",
        destructiveHint=False,
    ),
)
async def unity_index(
    ctx: Context,
    action: Annotated[
        Literal["status", "sync", "build"],
        "status: show index metadata; sync: incremental update (fast); build: full rebuild.",
    ] = "status",
) -> dict[str, Any]:
    result = await _insight_call(ctx, "index", [action])
    if not result.get("success"):
        return {"success": False, "message": result.get("error", "unknown error"),
                "data": {"stdout": result.get("stdout", "")}}
    return {"success": True, "message": f"Index {action} completed.", "data": {"output": result["stdout"]}}
