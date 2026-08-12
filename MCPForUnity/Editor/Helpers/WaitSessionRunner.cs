using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;

namespace MCPForUnity.Editor.Helpers
{
    /// <summary>
    /// Drives cross-domain-reload wait sessions: an operation ("wait until the editor is
    /// idle", "wait until this compile finishes") whose completion condition outlives the
    /// domain that started it — a compile that succeeds triggers a domain reload by
    /// definition, and the TCS an async HandleCommand holds dies with it.
    ///
    /// Sessions survive reloads the way Tuanjie's StepJobRunner does: the whole session
    /// (kind, fields, wall-clock deadline) is serialized to SessionState on every change and
    /// before assembly reload; after the reload a fresh tick is registered from
    /// [InitializeOnLoadMethod] and each session resumes from where it was.
    ///
    /// Delivery after a reload works differently than in the native bridge we studied: our
    /// transport drops the connection during domain reload, so the original async call can
    /// never be answered. Instead the Python client re-invokes the same tool with the same
    /// idempotency <see cref="WaitSession.Token"/> after reconnecting; the tool then either
    /// finds the completed result persisted under that token (<see cref="TryGetCompletedResult"/>)
    /// or attaches a fresh listener to the still-running session
    /// (<see cref="StartOrAttach"/>). Exactly one Python call gets the outcome per token.
    ///
    /// Timeout is wall-clock against the editor session clock
    /// (EditorApplication.timeSinceStartup), which does not reset on domain reload — time
    /// spent inside the reload counts against the deadline, matching Tuanjie.
    /// </summary>
    [InitializeOnLoad]
    internal static class WaitSessionRunner
    {
        private const string SessionKeySessions = "Mcp_WaitSessions";
        private const string SessionKeyResultPrefix = "Mcp_WaitResult_";
        private const string SessionKeyResultAtPrefix = "Mcp_WaitResultAt_";
        private const double ResultRetentionSeconds = 600.0;

        /// <summary>
        /// A single wait session. Subclasses implement <see cref="Kind"/> (persisted and used
        /// to rebuild after a reload) and <see cref="Tick"/> (advance one editor frame;
        /// return true when finished, having set the result via CompleteAsSuccess/CompleteAsError).
        /// All state lives in public fields so it round-trips through SessionState JSON.
        /// </summary>
        public abstract class WaitSession
        {
            /// <summary>Idempotency key chosen by the Python client; survives the reload.</summary>
            public string Token;
            /// <summary>Wall-clock deadline (EditorApplication.timeSinceStartup); 0 = no timeout.</summary>
            public double Deadline;
            public bool Completed;
            public bool TimedOut;
            /// <summary>Serialized SuccessResponse/ErrorResponse JSON delivered to listeners.</summary>
            public string ResultJson;
            /// <summary>timeSinceStartup when the session started; 0 until first tick.</summary>
            public double StartedAt;

            [JsonIgnore]
            public abstract string Kind { get; }

            /// <summary>
            /// Advance one frame. Return true when the session is finished; the implementation
            /// must have set ResultJson via CompleteAsSuccess/CompleteAsError (or HandleTimeout
            /// will fire first when the deadline passes).
            /// </summary>
            public abstract bool Tick();

            /// <summary>Called by the runner when the deadline passes. Default: generic timeout error.</summary>
            public virtual void HandleTimeout()
            {
                TimedOut = true;
                CompleteAsError(
                    $"Timed out after {(Deadline > 0 ? Math.Max(0, Deadline - EditorApplication.timeSinceStartup) : 0):F0}s waiting for editor.");
            }

            protected void CompleteAsSuccess(string message, object data)
            {
                ResultJson = JsonConvert.SerializeObject(new SuccessResponse(message, data));
                Completed = true;
            }

            internal void CompleteAsError(string error, object data = null)
            {
                ResultJson = JsonConvert.SerializeObject(new ErrorResponse(error, data));
                Completed = true;
            }
        }

        private sealed class PersistedSession
        {
            public string Kind;
            public string Json;
        }

        // Kind name -> factory used to rebuild sessions after a domain reload. Registered by
        // the session classes themselves via InitializeOnLoadMethod; loading is deferred to
        // the first tick / StartOrAttach so registration always happened first.
        private static readonly Dictionary<string, Func<WaitSession>> _factories =
            new Dictionary<string, Func<WaitSession>>(StringComparer.Ordinal);

        private static readonly List<WaitSession> _sessions = new List<WaitSession>();
        private static readonly Dictionary<string, List<TaskCompletionSource<string>>> _listeners =
            new Dictionary<string, List<TaskCompletionSource<string>>>(StringComparer.Ordinal);

        private static bool _loaded;
        private static bool _tickRegistered;

        static WaitSessionRunner()
        {
            EnsureTickRegistered();
        }

        /// <summary>Registers the factory used to rebuild sessions of <paramref name="kind"/> after a reload.</summary>
        public static void RegisterKind(string kind, Func<WaitSession> factory)
        {
            _factories[kind] = factory;
        }

        /// <summary>True when a completed result is persisted under this token; returns its response JSON.</summary>
        public static bool TryGetCompletedResult(string token, out string resultJson)
        {
            resultJson = null;
            if (string.IsNullOrEmpty(token))
                return false;

            string atKey = SessionKeyResultAtPrefix + token;
            string at = SessionState.GetString(atKey, null);
            string json = SessionState.GetString(SessionKeyResultPrefix + token, null);
            // Unity's SessionState.GetString(key, null) returns "" (not null) for missing
            // keys — treat empty as missing.
            if (string.IsNullOrEmpty(json))
                return false;

            // Expire stale results so SessionState does not grow unbounded.
            double now = EditorApplication.timeSinceStartup;
            if (!string.IsNullOrEmpty(at) && double.TryParse(at, out double storedAt)
                && now - storedAt > ResultRetentionSeconds)
            {
                SessionState.EraseString(SessionKeyResultPrefix + token);
                SessionState.EraseString(atKey);
                return false;
            }

            resultJson = json;
            return true;
        }

        /// <summary>
        /// Ensures a session for <paramref name="token"/> is running and returns it. When a
        /// session with the same token is already in flight (a re-invocation after reconnect),
        /// it is reused and the new listener is attached alongside the old one.
        /// </summary>
        public static WaitSession StartOrAttach(WaitSession session, TaskCompletionSource<string> tcs)
        {
            EnsureLoaded();
            EnsureTickRegistered();

            var existing = _sessions.Find(s => string.Equals(s.Token, session.Token, StringComparison.Ordinal));
            if (existing != null)
            {
                if (existing.Completed)
                {
                    // Finished between a failed transport and this re-invocation: hand the
                    // persisted outcome straight to the fresh listener.
                    if (tcs != null)
                    {
                        try { tcs.TrySetResult(existing.ResultJson); }
                        catch { /* listener already completed */ }
                    }
                    return existing;
                }
                if (tcs != null)
                    AttachListener(existing.Token, tcs);
                return existing;
            }

            _sessions.Add(session);
            if (tcs != null)
                AttachListener(session.Token, tcs);
            Save();

            // Wall-clock watchdog on the thread pool: the update tick (and with it the
            // deadline check) stalls when the editor window is unfocused, so a session could
            // otherwise hang the client forever. The watchdog only notifies listeners — the
            // main-thread tick performs the real cleanup when the editor wakes up.
            if (session.Deadline > 0)
            {
                double secondsLeft = session.Deadline - EditorApplication.timeSinceStartup;
                if (secondsLeft > 0)
                {
                    Task.Delay(TimeSpan.FromSeconds(secondsLeft))
                        .ContinueWith(t => FireWallTimeout(session), TaskScheduler.Default);
                }
            }
            return session;
        }

        private static void FireWallTimeout(WaitSession session)
        {
            if (session.Completed)
                return;
            // Do not touch session state here (the session list is main-thread only); just
            // deliver a timeout to whoever is waiting. The tick completes the cleanup.
            if (_listeners.TryGetValue(session.Token, out var list))
            {
                var json = JsonConvert.SerializeObject(new ErrorResponse(
                    $"Timed out waiting (wall clock) after {Math.Max(0, session.Deadline - EditorApplication.timeSinceStartup):F0}s for the editor."));
                foreach (var tcs in list)
                {
                    try { tcs.TrySetResult(json); }
                    catch { /* listener already completed */ }
                }
            }
        }

        /// <summary>Whether any session is currently in flight (diagnostics).</summary>
        public static int InFlightCount => _sessions.Count;

        /// <summary>Persists session state now (e.g. after the caller mutated a session field).</summary>
        public static void Persist()
        {
            Save();
        }

        private static void AttachListener(string token, TaskCompletionSource<string> tcs)
        {
            if (!_listeners.TryGetValue(token, out var list))
            {
                list = new List<TaskCompletionSource<string>>();
                _listeners[token] = list;
            }
            list.Add(tcs);
        }

        private static void EnsureTickRegistered()
        {
            if (_tickRegistered)
                return;
            _tickRegistered = true;
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private static void OnBeforeAssemblyReload()
        {
            Save();
            // TCSes die with the domain; drop them so a completed session in the next domain
            // does not TrySetResult on dead listeners (harmless, but they leak otherwise).
            _listeners.Clear();
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;
            _loaded = true;

            string json = SessionState.GetString(SessionKeySessions, null);
            if (string.IsNullOrEmpty(json))
                return;

            List<PersistedSession> persisted;
            try { persisted = JsonConvert.DeserializeObject<List<PersistedSession>>(json); }
            catch (Exception e)
            {
                McpLog.Warn($"[WaitSessionRunner] Failed to read persisted sessions: {e.Message}");
                SessionState.EraseString(SessionKeySessions);
                return;
            }
            if (persisted == null)
                return;

            foreach (var p in persisted)
            {
                var session = Rebuild(p);
                if (session != null)
                    _sessions.Add(session);
            }
        }

        private static WaitSession Rebuild(PersistedSession p)
        {
            try
            {
                if (!_factories.TryGetValue(p.Kind, out var factory))
                {
                    McpLog.Warn($"[WaitSessionRunner] No factory for session kind '{p.Kind}'; dropping it.");
                    return null;
                }
                var session = factory();
                JsonConvert.PopulateObject(p.Json, session);
                return session;
            }
            catch (Exception e)
            {
                McpLog.Warn($"[WaitSessionRunner] Failed to restore session '{p.Kind}': {e.Message}");
                return null;
            }
        }

        private static void Tick()
        {
            EnsureLoaded();

            if (_sessions.Count == 0)
                return;

            bool dirty = false;
            for (int i = _sessions.Count - 1; i >= 0; i--)
            {
                var session = _sessions[i];

                // A session restored already-completed (persisted between finish and removal)
                // is delivered and dropped on the first tick after the reload.
                if (session.Completed)
                {
                    _sessions.RemoveAt(i);
                    StoreResult(session);
                    NotifyListeners(session.Token, session.ResultJson);
                    dirty = true;
                    continue;
                }

                bool done = false;
                try
                {
                    done = session.Tick();
                }
                catch (Exception e)
                {
                    McpLog.Error($"[WaitSessionRunner] Session '{session.Kind}' failed: {e}");
                    session.CompleteAsError($"Wait session failed: {e.Message}");
                    done = true;
                }

                if (!done && session.Deadline > 0
                    && EditorApplication.timeSinceStartup > session.Deadline)
                {
                    try { session.HandleTimeout(); }
                    catch (Exception e)
                    {
                        McpLog.Warn($"[WaitSessionRunner] HandleTimeout threw for '{session.Kind}': {e.Message}");
                        session.CompleteAsError($"Timed out waiting for editor ({e.Message}).");
                    }
                    done = true;
                }

                if (!done)
                    continue;

                _sessions.RemoveAt(i);
                StoreResult(session);
                NotifyListeners(session.Token, session.ResultJson);
                dirty = true;
            }

            if (dirty)
                Save();
        }

        private static void StoreResult(WaitSession session)
        {
            // Results are keyed by token and expire lazily in TryGetCompletedResult
            // (ResultRetentionSeconds), which keeps SessionState bounded even for tokens
            // that are never queried again.
            string token = session.Token ?? Guid.NewGuid().ToString("N");
            SessionState.SetString(SessionKeyResultPrefix + token, session.ResultJson);
            SessionState.SetString(SessionKeyResultAtPrefix + token,
                EditorApplication.timeSinceStartup.ToString("R"));
        }

        private static void NotifyListeners(string token, string resultJson)
        {
            if (!_listeners.TryGetValue(token, out var list))
                return;
            _listeners.Remove(token);
            foreach (var tcs in list)
            {
                try { tcs.TrySetResult(resultJson); }
                catch { /* listener already completed */ }
            }
        }

        private static void Save()
        {
            if (_sessions.Count == 0)
            {
                SessionState.EraseString(SessionKeySessions);
                return;
            }

            try
            {
                var persisted = new List<PersistedSession>(_sessions.Count);
                foreach (var session in _sessions)
                {
                    persisted.Add(new PersistedSession
                    {
                        Kind = session.Kind,
                        Json = JsonConvert.SerializeObject(session),
                    });
                }
                SessionState.SetString(SessionKeySessions, JsonConvert.SerializeObject(persisted));
            }
            catch (Exception e)
            {
                McpLog.Warn($"[WaitSessionRunner] Failed to persist sessions: {e.Message}");
            }
        }
    }
}
