using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Thread-safe index of every in-flight asynchronous operation that should keep the
    /// editor from being reported "idle": test runs, package installs, execute_code
    /// coroutines, asset-generation jobs, and the wait tools themselves.
    ///
    /// The registry is what lets wait_for_idle answer "is anything still running?" without
    /// hard-coding every asynchronous tool in the codebase — a tool that wants its work to
    /// gate idle registers on start and unregisters when it finishes (see TestJobManager,
    /// PackageJobManager, ExecuteCode coroutines).
    ///
    /// Thread-safety: Register/Unregister/Snapshot may be called from any thread (some jobs
    /// finalize on background threads); the registry itself is lock-free for readers.
    /// </summary>
    internal static class JobRegistry
    {
        /// <summary>A registered in-flight job.</summary>
        public interface IJobHandle
        {
            string JobId { get; }
            /// <summary>Command name the job belongs to, e.g. "run_tests", "wait_for_idle".</summary>
            string Name { get; }
            /// <summary>Owning runner kind: "test", "package", "coroutine", "assetgen", "wait".</summary>
            string Runner { get; }
            long StartedUnixMs { get; }
        }

        private sealed class Handle : IJobHandle
        {
            public string JobId { get; }
            public string Name { get; }
            public string Runner { get; }
            public long StartedUnixMs { get; }

            public Handle(string jobId, string name, string runner)
            {
                JobId = jobId;
                Name = name;
                Runner = runner;
                StartedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        }

        private static readonly ConcurrentDictionary<string, IJobHandle> _byId =
            new ConcurrentDictionary<string, IJobHandle>(StringComparer.Ordinal);

        public static string Register(string name, string runner)
        {
            string jobId = Guid.NewGuid().ToString("N");
            _byId[jobId] = new Handle(jobId, name, runner);
            return jobId;
        }

        public static void Unregister(string jobId)
        {
            if (string.IsNullOrEmpty(jobId))
                return;
            _byId.TryRemove(jobId, out _);
        }

        /// <summary>
        /// Number of in-flight jobs, optionally excluding a name (a wait_for_idle session
        /// excludes its own kind so concurrent waits do not hold each other in flight).
        /// </summary>
        public static int Count(string excludeName = null)
        {
            int count = 0;
            foreach (var handle in _byId.Values)
            {
                if (excludeName != null
                    && string.Equals(handle.Name, excludeName, StringComparison.OrdinalIgnoreCase))
                    continue;
                count++;
            }
            return count;
        }

        /// <summary>Snapshot of every in-flight job, for diagnostics in wait results.</summary>
        public static List<IJobHandle> Snapshot()
        {
            var list = new List<IJobHandle>(_byId.Values);
            list.Sort((a, b) => a.StartedUnixMs.CompareTo(b.StartedUnixMs));
            return list;
        }
    }
}
