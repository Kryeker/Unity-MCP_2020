using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Helpers
{
    /// <summary>
    /// File-level snapshots for rollback of asset operations (delete/move/overwrite).
    ///
    /// Scope: file operations only — scene/prefab edits inside the editor go through
    /// Unity Undo instead and are deliberately not snapshotted here. Every snapshot
    /// bundles the asset file WITH its sibling .meta, so a rollback can never leave an
    /// asset orphaned from its GUID (which would break every reference to it).
    ///
    /// Snapshots persist under <project>/Library/McpSnapshots/<id>/ (file + .meta bytes
    /// verbatim plus a manifest), so they survive editor restarts. Each snapshot has a
    /// TTL; expired snapshots are cleaned lazily on capture and list.
    /// </summary>
    public static class SnapshotService
    {
        private const int MaxSnapshots = 20;
        private const double RetentionHours = 24.0;

        private sealed class SnapshotManifest
        {
            public string Id;
            public string CreatedAt;
            public List<SnapshotFile> Files = new List<SnapshotFile>();
        }

        private sealed class SnapshotFile
        {
            public string AssetPath;   // project-relative, e.g. Assets/Foo.mat
            public string FileName;    // stored file name on disk (may be the .meta)
            public long Length;
            public bool IsMeta;
        }

        private static readonly object _lock = new object();
        private static string _root;

        private static string RootDir()
        {
            if (_root == null)
            {
                _root = Path.Combine(Application.dataPath, "..", "Library", "McpSnapshots");
                Directory.CreateDirectory(_root);
            }
            return _root;
        }

        /// <summary>
        /// Expands project-relative paths (files or folders) to the concrete files that an
        /// operation would touch — every asset file plus its sibling .meta. Used both for
        /// snapshots and for dry-run previews.
        /// </summary>
        public static List<string> ExpandPaths(IEnumerable<string> assetPaths)
        {
            var result = new List<string>();
            foreach (var rawPath in assetPaths)
            {
                string assetPath = AssetPathUtility.SanitizeAssetPath(rawPath);
                if (string.IsNullOrEmpty(assetPath) || assetPath == "Assets")
                    continue;
                string abs = ToAbsolute(assetPath);
                if (Directory.Exists(abs))
                {
                    foreach (var file in Directory.GetFiles(abs, "*", SearchOption.AllDirectories))
                    {
                        string rel = ToProjectRelative(file);
                        result.Add(rel);
                        if (File.Exists(file + ".meta"))
                            result.Add(rel + ".meta");
                    }
                }
                else if (File.Exists(abs))
                {
                    result.Add(assetPath);
                    if (File.Exists(abs + ".meta"))
                        result.Add(assetPath + ".meta");
                }
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Captures the current bytes of the given project-relative asset paths (folders are
        /// expanded recursively, each asset bundled with its sibling .meta) into a new
        /// snapshot. Returns the snapshot id, or null when nothing was captured.
        /// </summary>
        public static string Capture(IEnumerable<string> assetPaths)
        {
            lock (_lock)
            {
                ExpireOld();
                var files = new List<SnapshotFile>();
                foreach (var p in ExpandPaths(assetPaths))
                {
                    string abs = ToAbsolute(p);
                    files.Add(new SnapshotFile
                    {
                        AssetPath = p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                            ? p.Substring(0, p.Length - 5)
                            : p,
                        FileName = Path.GetFileName(p),
                        Length = new FileInfo(abs).Length,
                        IsMeta = p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase),
                    });
                }

                if (files.Count == 0)
                    return null;

                string id = Guid.NewGuid().ToString("N");
                string dir = Path.Combine(RootDir(), id);
                Directory.CreateDirectory(dir);

                foreach (var f in files)
                {
                    string src = ToAbsolute(f.IsMeta ? f.AssetPath + ".meta" : f.AssetPath);
                    File.Copy(src, Path.Combine(dir, f.FileName), true);
                }

                var manifest = new SnapshotManifest
                {
                    Id = id,
                    CreatedAt = DateTime.UtcNow.ToString("o"),
                    Files = files,
                };
                File.WriteAllText(Path.Combine(dir, "manifest.json"),
                    JsonConvert.SerializeObject(manifest, Formatting.Indented));

                PruneIfNeeded();
                return id;
            }
        }

        /// <summary>
        /// Restores every file in the snapshot (asset bytes + .meta) and refreshes the
        /// asset database. Returns a per-file report.
        /// </summary>
        public static object Rollback(string snapshotId)
        {
            lock (_lock)
            {
                var result = new List<object>();
                string dir = Path.Combine(RootDir(), snapshotId ?? string.Empty);
                string manifestPath = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifestPath))
                    return new ErrorResponse($"Snapshot '{snapshotId}' not found.");

                SnapshotManifest manifest;
                try { manifest = JsonConvert.DeserializeObject<SnapshotManifest>(File.ReadAllText(manifestPath)); }
                catch (Exception e) { return new ErrorResponse($"Failed to read snapshot manifest: {e.Message}"); }

                var restored = new List<string>();
                foreach (var f in manifest.Files)
                {
                    string stored = Path.Combine(dir, f.FileName);
                    if (!File.Exists(stored))
                    {
                        result.Add(new { path = f.AssetPath, ok = false, error = "snapshot file missing" });
                        continue;
                    }
                    try
                    {
                        string target = ToAbsolute(f.IsMeta ? f.AssetPath + ".meta" : f.AssetPath);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(stored, target, overwrite: true);
                        restored.Add(target);
                        result.Add(new { path = f.AssetPath, ok = true });
                    }
                    catch (Exception e)
                    {
                        result.Add(new { path = f.AssetPath, ok = false, error = e.Message });
                    }
                }

                if (restored.Count > 0)
                    AssetDatabase.Refresh();

                return new SuccessResponse(
                    $"Rolled back snapshot '{snapshotId}' ({result.Count} file(s)).",
                    new { snapshotId, files = result });
            }
        }

        /// <summary>Lists snapshots (id, age, file count, total bytes).</summary>
        public static object List()
        {
            lock (_lock)
            {
                ExpireOld();
                var snapshots = new List<object>();
                foreach (var dir in Directory.GetDirectories(RootDir()))
                {
                    string manifestPath = Path.Combine(dir, "manifest.json");
                    if (!File.Exists(manifestPath))
                        continue;
                    try
                    {
                        var manifest = JsonConvert.DeserializeObject<SnapshotManifest>(File.ReadAllText(manifestPath));
                        long totalBytes = manifest.Files.Sum(f => f.Length);
                        snapshots.Add(new
                        {
                            id = Path.GetFileName(dir),
                            created_at = manifest.CreatedAt,
                            files = manifest.Files.Count,
                            total_bytes = totalBytes,
                            paths = manifest.Files.Where(f => !f.IsMeta).Select(f => f.AssetPath).ToList(),
                        });
                    }
                    catch { /* skip corrupt manifests */ }
                }
                return new SuccessResponse($"{snapshots.Count} snapshot(s).", new { snapshots });
            }
        }

        /// <summary>Deletes a snapshot (or all when snapshotId is null).</summary>
        public static object Delete(string snapshotId)
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(snapshotId))
                {
                    int count = Directory.GetDirectories(RootDir()).Length;
                    foreach (var dir in Directory.GetDirectories(RootDir()))
                        Directory.Delete(dir, recursive: true);
                    return new SuccessResponse($"Deleted {count} snapshot(s).");
                }

                string snapshotDir = Path.Combine(RootDir(), snapshotId);
                if (!Directory.Exists(snapshotDir))
                    return new ErrorResponse($"Snapshot '{snapshotId}' not found.");
                Directory.Delete(snapshotDir, recursive: true);
                return new SuccessResponse($"Deleted snapshot '{snapshotId}'.");
            }
        }

        /// <summary>Project-relative path -> absolute file path (Assets root aware).</summary>
        private static string ToAbsolute(string projectRelativePath)
        {
            string normalized = projectRelativePath.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalized))
                return normalized;
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", normalized));
        }

        private static string ToProjectRelative(string absolutePath)
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string full = Path.GetFullPath(absolutePath);
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return full.Substring(root.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
            return full;
        }

        private static void ExpireOld()
        {
            var cutoff = DateTime.UtcNow.AddHours(-RetentionHours);
            foreach (var dir in Directory.GetDirectories(RootDir()))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                        Directory.Delete(dir, recursive: true);
                }
                catch { /* best effort */ }
            }
        }

        private static void PruneIfNeeded()
        {
            var dirs = Directory.GetDirectories(RootDir())
                .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
                .ToList();
            while (dirs.Count > MaxSnapshots)
            {
                try { Directory.Delete(dirs[dirs.Count - 1], recursive: true); }
                catch { /* best effort */ }
                dirs.RemoveAt(dirs.Count - 1);
            }
        }
    }
}
