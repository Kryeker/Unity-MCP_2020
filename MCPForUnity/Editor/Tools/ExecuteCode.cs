using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using MCPForUnity.Runtime.Helpers;
using Microsoft.CSharp;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    [McpForUnityTool("execute_code", AutoRegister = false, Group = "scripting_ext", ConfirmationLevel = ConfirmationLevel.Always)]
    public static class ExecuteCode
    {
        private const int MaxCodeLength = 50000;
        private const int MaxHistoryEntries = 50;
        private const int MaxHistoryCodePreview = 500;
        private const int MaxLogRing = 500;
        private const int MaxLogEntriesPerRun = 200;
        internal const int WrapperLineOffset = 10;
        private const string WrapperClassName = "MCPDynamicCode";
        private const string WrapperMethodName = "Execute";
        private const string ActionExecute = "execute";
        private const string ActionGetHistory = "get_history";
        private const string ActionClearHistory = "clear_history";
        private const string ActionReplay = "replay";

        private const string ModeSync = "sync";
        private const string ModeCoroutine = "coroutine";

        private static readonly List<HistoryEntry> _history = new List<HistoryEntry>();
        private static string[] _cachedAssemblyPaths;

        // Global ring buffer of editor log messages, captured via Application.logMessageReceived
        // (main thread). Each execute call snapshots a cursor and returns the entries added while
        // it ran — sync or coroutine. The buffer survives domain reloads? No: statics reset, so
        // the ring restarts empty after a reload, which is fine — a reload also kills any
        // in-flight coroutine, and its interruption is reported separately.
        private static readonly List<LogEntry> _logRing = new List<LogEntry>(MaxLogRing + 1);

        // Set by coroutine code via SetCoroutineResult; reset at the start of each coroutine run.
        private static object _coroutineResult;
        private static CoroutineHost _coroutineHost;
        private static readonly object _coroutineLock = new object();

        // Unity throttles the player loop (and with it the coroutine pump) when the editor
        // window loses focus. Coroutine executions force runInBackground while they run,
        // reference-counted so concurrent coroutines restore the user's setting correctly.
        private static int _activeCoroutines;

        [UnityEditor.InitializeOnLoadMethod]
        private static void OnDomainReload()
        {
            _cachedAssemblyPaths = null;
            _coroutineResult = null;
            _coroutineHost = null;
            lock (_logRing)
            {
                _logRing.Clear();
            }
            lock (_codedomReadableCache)
            {
                _codedomReadableCache.Clear();
            }
            Application.logMessageReceived -= OnLogMessage;
            Application.logMessageReceived += OnLogMessage;
            RoslynCompiler.ResetCache();
        }

        private static readonly HashSet<string> _blockedPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.IO.File.Delete",
            "System.IO.Directory.Delete",
            "FileUtil.DeleteFileOrDirectory",
            "AssetDatabase.DeleteAsset",
            "AssetDatabase.MoveAssetToTrash",
            "EditorApplication.Exit",
            "Process.Start",
            "Process.Kill",
            "while(true)",
            "while (true)",
            "for(;;)",
            "for (;;)",
        };

        public static async Task<object> HandleCommand(JObject @params)
        {
            if (@params == null)
                return new ErrorResponse("Parameters cannot be null.");

            var p = new ToolParams(@params);
            var actionResult = p.GetRequired("action");
            if (!actionResult.IsSuccess)
                return new ErrorResponse(actionResult.ErrorMessage);

            string action = actionResult.Value.ToLowerInvariant();

            switch (action)
            {
                case ActionExecute:
                    return await HandleExecute(@params);
                case ActionGetHistory:
                    return HandleGetHistory(@params);
                case ActionClearHistory:
                    return HandleClearHistory();
                case ActionReplay:
                    return await HandleReplay(@params);
                default:
                    return new ErrorResponse(
                        $"Unknown action: '{action}'. Valid actions: {ActionExecute}, {ActionGetHistory}, {ActionClearHistory}, {ActionReplay}");
            }
        }

        private static async Task<object> HandleExecute(JObject @params)
        {
            string code = @params["code"]?.ToString();
            if (string.IsNullOrWhiteSpace(code))
                return new ErrorResponse("Required parameter 'code' is missing or empty.");

            if (code.Length > MaxCodeLength)
                return new ErrorResponse($"Code exceeds maximum length of {MaxCodeLength} characters.");

            bool safetyChecks = @params["safety_checks"]?.Value<bool>() ?? true;
            string compiler = @params["compiler"]?.ToString()?.ToLowerInvariant() ?? "auto";
            string executionMode = @params["execution_mode"]?.ToString()?.ToLowerInvariant() ?? ModeSync;
            if (executionMode != ModeSync && executionMode != ModeCoroutine)
                return new ErrorResponse($"Unknown execution_mode: '{executionMode}'. Valid modes: {ModeSync}, {ModeCoroutine}.");
            bool captureLogs = @params["capture_logs"]?.Value<bool>() ?? true;
            int logLimit = Mathf.Clamp(@params["log_limit"]?.Value<int>() ?? MaxLogEntriesPerRun, 1, MaxLogEntriesPerRun);
            int timeoutSeconds = Mathf.Clamp(@params["timeout_s"]?.Value<int>() ?? 60, 1, 600);

            if (safetyChecks)
            {
                var violation = CheckBlockedPatterns(code);
                if (violation != null)
                    return new ErrorResponse($"Blocked pattern detected: {violation}");
            }

            try
            {
                var startTime = DateTime.UtcNow;
                int logCursor = captureLogs ? SnapshotLogCursor() : -1;

                object result;
                if (executionMode == ModeCoroutine)
                    result = await InvokeCoroutineAsync(code, compiler, timeoutSeconds);
                else
                    result = CompileAndExecute(code, compiler);

                var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

                if (captureLogs)
                    result = AttachLogs(result, logCursor, logLimit);

                AddToHistory(code, result, elapsed, safetyChecks, compiler, executionMode);
                return result;
            }
            catch (Exception e)
            {
                McpLog.Error($"[ExecuteCode] Execution failed: {e}");
                var errorResult = new ErrorResponse($"Execution failed: {e.Message}");
                AddToHistory(code, errorResult, 0, safetyChecks, compiler, executionMode);
                return errorResult;
            }
        }

        private static object HandleGetHistory(JObject @params)
        {
            int limit = @params["limit"]?.Value<int>() ?? 10;
            limit = Mathf.Clamp(limit, 1, MaxHistoryEntries);

            if (_history.Count == 0)
                return new SuccessResponse("No execution history.", new { total = 0, entries = new object[0] });

            var entries = _history.Skip(Math.Max(0, _history.Count - limit)).ToList();
            return new SuccessResponse($"Returning {entries.Count} of {_history.Count} history entries.", new
            {
                total = _history.Count,
                entries = entries.Select((e, i) => new
                {
                    index = _history.Count - entries.Count + i,
                    codePreview = e.code.Length > MaxHistoryCodePreview
                        ? e.code.Substring(0, MaxHistoryCodePreview) + "..."
                        : e.code,
                    e.success,
                    e.resultPreview,
                    e.elapsedMs,
                    e.timestamp,
                    e.safetyChecksEnabled,
                    e.compiler,
                }).ToList(),
            });
        }

        private static object HandleClearHistory()
        {
            int count = _history.Count;
            _history.Clear();
            return new SuccessResponse($"Cleared {count} history entries.");
        }

        private static async Task<object> HandleReplay(JObject @params)
        {
            if (_history.Count == 0)
                return new ErrorResponse("No execution history to replay.");

            int? index = @params["index"]?.Value<int>();
            if (index == null || index < 0 || index >= _history.Count)
                return new ErrorResponse($"Invalid history index. Valid range: 0-{_history.Count - 1}");

            var entry = _history[index.Value];
            var replayParams = JObject.FromObject(new
            {
                action = ActionExecute,
                code = entry.code,
                safety_checks = entry.safetyChecksEnabled,
                compiler = entry.compiler ?? "auto",
                execution_mode = entry.executionMode ?? ModeSync,
            });
            return await HandleExecute(replayParams);
        }

        // ──────────────────── Log capture ────────────────────

        private sealed class LogEntry
        {
            public string message;
            public string type;      // "error" / "warning" / "log"
            public string timestamp; // yyyy-MM-ddTHH:mm:ss.fffZ
        }

        private static void OnLogMessage(string message, string stackTrace, LogType type)
        {
            lock (_logRing)
            {
                _logRing.Add(new LogEntry
                {
                    message = message ?? string.Empty,
                    type = TypeName(type),
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                });
                if (_logRing.Count > MaxLogRing)
                    _logRing.RemoveRange(0, _logRing.Count - MaxLogRing);
            }
        }

        private static string TypeName(LogType type)
        {
            switch (type)
            {
                case LogType.Error:
                case LogType.Exception:
                case LogType.Assert:
                    return "error";
                case LogType.Warning:
                    return "warning";
                default:
                    return "log";
            }
        }

        private static int SnapshotLogCursor()
        {
            lock (_logRing)
            {
                return _logRing.Count;
            }
        }

        /// <summary>
        /// Attaches the log entries added since <paramref name="cursor"/> to the response's
        /// data payload. Works on any response shape (SuccessResponse or ErrorResponse);
        /// when the response has no data, the logs are still reported.
        /// </summary>
        private static object AttachLogs(object result, int cursor, int logLimit)
        {
            if (cursor < 0)
                return result;

            List<LogEntry> captured;
            lock (_logRing)
            {
                int start = Math.Max(0, cursor);
                int count = _logRing.Count - start;
                if (count <= 0)
                    return result;
                captured = _logRing.GetRange(start, Math.Min(count, logLimit));
            }

            var logs = captured.Select(e => new
            {
                message = e.message,
                type = e.type,
                timestamp = e.timestamp,
            }).ToArray();

            JObject payload;
            if (result is SuccessResponse sr)
            {
                JObject data = CoerceData(sr.Data);
                data["logs"] = JArray.FromObject(logs);
                data["logs_total"] = _logRing.Count - cursor;
                payload = JObject.FromObject(new
                {
                    success = true,
                    message = sr.Message,
                    data = data,
                });
            }
            else if (result is ErrorResponse er)
            {
                JObject data = CoerceData(er.Data);
                data["logs"] = JArray.FromObject(logs);
                data["logs_total"] = _logRing.Count - cursor;
                payload = JObject.FromObject(new
                {
                    success = false,
                    error = er.Error,
                    data = data,
                });
            }
            else
            {
                return result;
            }

            return payload;
        }

        /// <summary>Response data may be an anonymous object (InvokeCompiled) or a JObject; normalize to JObject.</summary>
        private static JObject CoerceData(object data)
        {
            if (data is JObject jo)
                return jo;
            if (data != null)
            {
                try { return JObject.FromObject(data); }
                catch { /* fall through */ }
            }
            return new JObject();
        }

        // ──────────────────── Compilation ────────────────────

        private static object CompileAndExecute(string code, string compiler)
        {
            var compiled = CompileToAssembly(WrapUserCode(code), compiler, out var usedCompiler, out var error);
            if (compiled == null)
                return error;
            return InvokeCompiled(compiled, usedCompiler);
        }

        /// <summary>
        /// Compiles the wrapped source and returns the assembly; on failure returns null and
        /// sets <paramref name="error"/> to the ErrorResponse to surface.
        /// </summary>
        private static Assembly CompileToAssembly(string wrappedSource, string compiler,
            out string usedCompiler, out object error)
        {
            error = null;
            usedCompiler = null;
            string[] assemblyPaths = GetAssemblyPaths();

            Assembly compiled;
            switch (compiler)
            {
                case "roslyn":
                    if (!RoslynCompiler.IsAvailable)
                    {
                        error = new ErrorResponse("Roslyn (Microsoft.CodeAnalysis) is not available. Install it via NuGet or use compiler='codedom'.");
                        return null;
                    }
                    compiled = RoslynCompiler.Compile(wrappedSource, assemblyPaths, out var roslynErrors);
                    if (compiled == null)
                    {
                        error = new ErrorResponse("Compilation failed", new { errors = OffsetErrors(roslynErrors), compiler = "roslyn" });
                        return null;
                    }
                    usedCompiler = "roslyn";
                    break;

                case "codedom":
                    compiled = CodeDomCompile(wrappedSource, assemblyPaths, out var codedomErrors);
                    if (compiled == null)
                    {
                        error = new ErrorResponse("Compilation failed", new { errors = OffsetErrors(codedomErrors), compiler = "codedom" });
                        return null;
                    }
                    usedCompiler = "codedom";
                    break;

                default: // "auto"
                    if (RoslynCompiler.IsAvailable)
                    {
                        compiled = RoslynCompiler.Compile(wrappedSource, assemblyPaths, out var autoErrors);
                        if (compiled == null)
                        {
                            error = new ErrorResponse("Compilation failed", new { errors = OffsetErrors(autoErrors), compiler = "roslyn" });
                            return null;
                        }
                        usedCompiler = "roslyn";
                    }
                    else
                    {
                        compiled = CodeDomCompile(wrappedSource, assemblyPaths, out var autoFallbackErrors);
                        if (compiled == null)
                        {
                            error = new ErrorResponse("Compilation failed", new { errors = OffsetErrors(autoFallbackErrors), compiler = "codedom" });
                            return null;
                        }
                        usedCompiler = "codedom";
                    }
                    break;
            }

            return compiled;
        }

        private static object InvokeCompiled(Assembly assembly, string compilerUsed)
        {
            var type = assembly.GetType(WrapperClassName);
            if (type == null)
                return new ErrorResponse("Internal error: failed to find compiled type.");

            var method = type.GetMethod(WrapperMethodName, BindingFlags.Public | BindingFlags.Static);
            if (method == null)
                return new ErrorResponse("Internal error: failed to find Execute method.");

            object result = null;
            Exception executionError = null;

            try
            {
                result = method.Invoke(null, null);
            }
            catch (TargetInvocationException tie)
            {
                executionError = tie.InnerException ?? tie;
            }
            catch (Exception e)
            {
                executionError = e;
            }

            if (executionError != null)
                return new ErrorResponse($"Runtime error: {executionError.Message}",
                    new { exceptionType = executionError.GetType().Name, stackTrace = executionError.StackTrace, compiler = compilerUsed });

            if (result != null)
                return new SuccessResponse("Code executed successfully.",
                    new { result = SerializeResult(result), compiler = compilerUsed });

            return new SuccessResponse("Code executed successfully.", new { compiler = compilerUsed });
        }

        private static List<string> OffsetErrors(List<string> errors)
        {
            // Errors already have line numbers adjusted by the compiler-specific code
            return errors;
        }

        // ──────────────────── CodeDom compiler ────────────────────

        private static Assembly CodeDomCompile(string source, string[] assemblyPaths, out List<string> errors)
        {
            errors = new List<string>();

            // CodeDom needs the netstandard-aware filtered paths
            var filtered = FilterAssemblyPathsForCodeDom(assemblyPaths);

            // CSharpCodeProvider turns every ReferencedAssemblies entry into a literal /r:"..." flag
            // on the csc command line. Projects with ~100+ asmdefs blow past Windows' 32 KB
            // CreateProcess argument limit and fail with "The filename or extension is too long."
            // Route references through a response file (@responsefile is supported by both mcs and
            // Roslyn csc) so we pass exactly one short argument regardless of reference count.
            string responseFilePath = Path.Combine(Path.GetTempPath(), $"mcp-codedom-{Guid.NewGuid():N}.rsp");

            try
            {
                using (var writer = new StreamWriter(responseFilePath, append: false, Encoding.UTF8))
                {
                    foreach (var path in filtered)
                    {
                        writer.Write("/r:\"");
                        writer.Write(path);
                        writer.WriteLine("\"");
                    }
                }

                using (var provider = new CSharpCodeProvider())
                {
                    var parameters = new CompilerParameters
                    {
                        GenerateInMemory = true,
                        GenerateExecutable = false,
                        TreatWarningsAsErrors = false,
                        CompilerOptions = "@\"" + responseFilePath + "\"",
                    };

                    var results = provider.CompileAssemblyFromSource(parameters, source);

                    if (results.Errors.HasErrors)
                    {
                        foreach (CompilerError error in results.Errors)
                        {
                            if (!error.IsWarning)
                            {
                                int userLine = Math.Max(1, error.Line - WrapperLineOffset);
                                errors.Add($"Line {userLine}: {error.ErrorText}");
                            }
                        }
                        return null;
                    }

                    return results.CompiledAssembly;
                }
            }
            finally
            {
                try { if (File.Exists(responseFilePath)) File.Delete(responseFilePath); }
                catch { /* best effort */ }
            }
        }

        // CSharpCodeProvider can't resolve type-forwarding, so when netstandard.dll is loaded
        // alongside mscorlib/System.Runtime/System.Collections, types like List<T> appear in
        // multiple assemblies causing "type defined multiple times" errors.
        private static readonly HashSet<string> _codedomDuplicateAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mscorlib",
            "System.Runtime",
            "System.Private.CoreLib",
            "System.Collections",
        };

        private static string[] FilterAssemblyPathsForCodeDom(string[] allPaths)
        {
            bool hasNetstandard = allPaths.Any(p =>
                string.Equals(Path.GetFileNameWithoutExtension(p), "netstandard", StringComparison.OrdinalIgnoreCase));

            var filtered = allPaths;
            if (hasNetstandard)
            {
                filtered = allPaths.Where(p =>
                    !_codedomDuplicateAssemblies.Contains(Path.GetFileNameWithoutExtension(p))).ToArray();
            }

            // CodeDom compiles with the .NET Framework csc, which rejects assemblies built for
            // netstandard2.1+ / .NET Core — e.g. com.unity.nuget.newtonsoft-json@3.x and
            // collab-proxy's bundled Newtonsoft fail with "does not contain valid metadata",
            // taking down the whole compile. AssemblyName.GetAssemblyName still succeeds on
            // those, so probe the target framework instead and drop unreadable ones; code that
            // needs those types then fails with a clear "type not found" instead of a cryptic
            // metadata error. Roslyn is unaffected.
            var readable = new List<string>();
            foreach (var path in filtered)
            {
                if (IsCodeDomReadable(path))
                    readable.Add(path);
                else
                    McpLog.Warn($"[ExecuteCode] CodeDom: skipping unreadable assembly {Path.GetFileName(path)}");
            }
            return readable.ToArray();
        }

        private static readonly Dictionary<string, bool> _codedomReadableCache =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// True when the CodeDom compiler can consume this assembly. Mono's mcs (the compiler
        /// behind CSharpCodeProvider on Unity's .NET 4.x runtime) rejects assemblies carrying
        /// C# 8 nullable metadata (NullableContextAttribute / NullableAttribute): prebuilt
        /// nuget assemblies — e.g. com.unity.nuget.newtonsoft-json@3.x, collab-proxy's bundled
        /// Newtonsoft — fail the whole compile with "does not contain valid metadata", while
        /// assemblies the editor itself builds (no nullable attributes; verified on 2020.3)
        /// load fine. Probe for the attribute names in the #Strings heap.
        /// </summary>
        private static bool IsCodeDomReadable(string path)
        {
            bool cached;
            lock (_codedomReadableCache)
            {
                if (_codedomReadableCache.TryGetValue(path, out cached))
                    return cached;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                cached = !ContainsAscii(bytes, "NullableContextAttribute")
                    && !ContainsAscii(bytes, "NullableAttribute");
                if (!cached)
                    McpLog.Warn($"[ExecuteCode] CodeDom: skipping nullable-metadata assembly {Path.GetFileName(path)}");
            }
            catch (Exception e)
            {
                // Unreadable for an unexpected reason — let csc decide rather than dropping
                // an assembly the user may actually need.
                McpLog.Warn($"[ExecuteCode] CodeDom probe failed for {Path.GetFileName(path)}: {e.Message}");
                cached = true;
            }

            lock (_codedomReadableCache)
            {
                _codedomReadableCache[path] = cached;
            }
            return cached;
        }

        /// <summary>Plain-ASCII substring search over raw file bytes (the #Strings heap stores type names verbatim).</summary>
        private static bool ContainsAscii(byte[] haystack, string needle)
        {
            var n = Encoding.ASCII.GetBytes(needle);
            if (n.Length == 0 || n.Length > haystack.Length)
                return false;
            for (int i = 0; i <= haystack.Length - n.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < n.Length; j++)
                {
                    if (haystack[i + j] != n[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return true;
            }
            return false;
        }


        // ──────────────────── Shared helpers ────────────────────

        private static string WrapUserCode(string code)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Reflection;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine("using UnityEditor;");
            sb.AppendLine($"public static class {WrapperClassName}");
            sb.AppendLine("{");
            sb.AppendLine($"    public static object {WrapperMethodName}()");
            sb.AppendLine("    {");
            sb.AppendLine(code);
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// Coroutine-mode wrapper: the user body lives in a method returning IEnumerator, so it
        /// may contain <c>yield return</c> (WaitForSeconds, null, AsyncOperation, nested
        /// IEnumerator...). Line offset matches <see cref="WrapperLineOffset"/>.
        /// </summary>
        private static string WrapUserCodeCoroutine(string code)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Reflection;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine("using UnityEditor;");
            sb.AppendLine($"public static class {WrapperClassName}");
            sb.AppendLine("{");
            sb.AppendLine($"    public static IEnumerator {WrapperMethodName}()");
            sb.AppendLine("    {");
            sb.AppendLine(code);
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // ──────────────────── Coroutine mode ────────────────────

        /// <summary>
        /// Sets the result value a coroutine-mode execution returns. Call from user code:
        /// <c>MCPForUnity.Editor.Tools.ExecuteCode.SetCoroutineResult("done");</c>
        /// </summary>
        public static void SetCoroutineResult(object result)
        {
            _coroutineResult = result;
        }

        private static async Task<object> InvokeCoroutineAsync(string code, string compiler, int timeoutSeconds)
        {
            var compiled = CompileToAssembly(WrapUserCodeCoroutine(code), compiler, out var usedCompiler, out var error);
            if (compiled == null)
                return error;

            var type = compiled.GetType(WrapperClassName);
            var method = type?.GetMethod(WrapperMethodName, BindingFlags.Public | BindingFlags.Static);
            if (method == null)
                return new ErrorResponse("Internal error: failed to find Execute method.");

            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Gate wait_for_idle on this coroutine while it runs.
            string jobId = JobRegistry.Register("execute_code", "coroutine");
            EnterCoroutine();

            // Wall-clock timeout watchdog on the thread pool: the coroutine pump can stall
            // when the editor window is unfocused, so the Update-based timeout would never
            // fire — this guarantees the call returns even then.
            var watchdogCts = new System.Threading.CancellationTokenSource();
            Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), watchdogCts.Token)
                .ContinueWith(t =>
                {
                    if (!t.IsCanceled)
                        tcs.TrySetResult(new ErrorResponse(
                            $"Coroutine timed out after {timeoutSeconds}s (wall clock). The coroutine was stopped; any work it had queued is abandoned."));
                }, TaskScheduler.Default);

            try
            {
                lock (_coroutineLock)
                {
                    _coroutineResult = null;
                    var go = new GameObject("MCPForUnity_ExecuteCodeHost");
                    go.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
                    var host = go.AddComponent<CoroutineHost>();
                    host.Compiler = usedCompiler;
                    host.Tcs = tcs;
                    host.TimeoutSeconds = timeoutSeconds;
                    host.StartedAt = EditorApplication.timeSinceStartup;
                    host.StartCoroutine(host.Run(method));
                    _coroutineHost = host;
                }

                return await tcs.Task.ConfigureAwait(true);
            }
            finally
            {
                watchdogCts.Cancel();
                watchdogCts.Dispose();
                JobRegistry.Unregister(jobId);
                ExitCoroutine();
            }
        }

        private static void EnterCoroutine()
        {
            if (System.Threading.Interlocked.Increment(ref _activeCoroutines) == 1)
                Application.runInBackground = true;
        }

        private static void ExitCoroutine()
        {
            if (System.Threading.Interlocked.Decrement(ref _activeCoroutines) <= 0)
            {
                _activeCoroutines = 0;
                Application.runInBackground = false;
            }
        }

        /// <summary>
        /// Runs a user coroutine on Unity's own coroutine pump. The wrapping iterator forwards
        /// every MoveNext result to the pump (so yield instructions behave natively) while
        /// catching user exceptions into the response — Unity's bare "Coroutine failed" log
        /// carries no structured error. The user enumerator is disposed on every exit path
        /// (a C# iterator's finally blocks run only on Dispose, not on a caught throw).
        /// </summary>
        private sealed class CoroutineHost : MonoBehaviour
        {
            public string Compiler;
            public TaskCompletionSource<object> Tcs;
            public double TimeoutSeconds;
            public double StartedAt;
            public bool Finished;

            public IEnumerator Run(MethodInfo method)
            {
                IEnumerator inner = null;
                try
                {
                    try
                    {
                        inner = method.Invoke(null, null) as IEnumerator;
                    }
                    catch (Exception e)
                    {
                        Fail(e);
                        yield break;
                    }

                    if (inner == null)
                    {
                        Fail(new InvalidOperationException(
                            "Coroutine mode requires the code to yield — e.g. 'yield return null;' or 'yield return new WaitForSeconds(1);'."));
                        yield break;
                    }

                    McpLog.Info($"[ExecuteCode] coroutine started: {method.DeclaringType?.Assembly.GetName().Name}");
                    int steps = 0;
                    while (true)
                    {
                        bool moved;
                        try
                        {
                            moved = inner.MoveNext();
                        }
                        catch (Exception e)
                        {
                            Fail(e);
                            yield break;
                        }
                        if (!moved)
                            break;
                        // yield sits outside any catch block (C# forbids yielding inside a
                        // try-with-catch); Unity's pump interprets the forwarded instruction.
                        yield return inner.Current;
                        steps++;
                        if (steps % 10 == 0)
                            McpLog.Info($"[ExecuteCode] coroutine step {steps}");
                    }
                    McpLog.Info($"[ExecuteCode] coroutine finished after {steps} steps");

                    var result = _coroutineResult;
                    if (result != null)
                        Tcs.TrySetResult(new SuccessResponse("Coroutine completed.",
                            new { result = SerializeResult(result), compiler = Compiler }));
                    else
                        Tcs.TrySetResult(new SuccessResponse("Coroutine completed.", new { compiler = Compiler }));
                }
                finally
                {
                    // A C# iterator's finally runs on Dispose or natural end, never on a caught
                    // throw — so user cleanup (their finally blocks) only runs if we dispose the
                    // enumerator ourselves on every exit path.
                    try { if (inner is IDisposable disposable) disposable.Dispose(); } catch { }
                    lock (_coroutineLock)
                    {
                        if (ReferenceEquals(_coroutineHost, this))
                            _coroutineHost = null;
                    }
                    Finished = true;
                }
            }

            private void Fail(Exception e)
            {
                var innerEx = (e is TargetInvocationException tie) ? (tie.InnerException ?? e) : e;
                Tcs.TrySetResult(new ErrorResponse($"Coroutine error: {innerEx.Message}",
                    new { exceptionType = innerEx.GetType().Name, stackTrace = innerEx.StackTrace, compiler = Compiler }));
            }

            private void Update()
            {
                if (Finished)
                    return;
                if (TimeoutSeconds > 0 && EditorApplication.timeSinceStartup - StartedAt > TimeoutSeconds)
                {
                    Tcs.TrySetResult(new ErrorResponse(
                        $"Coroutine timed out after {TimeoutSeconds}s. The coroutine was stopped; any work it had queued is abandoned."));
                    StopAllCoroutines();
                    Destroy(gameObject);
                }
            }
        }

        private static string[] GetAssemblyPaths()
        {
            if (_cachedAssemblyPaths == null)
                _cachedAssemblyPaths = ResolveAssemblyPaths();
            return _cachedAssemblyPaths;
        }

        private static string[] ResolveAssemblyPaths()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var assembly in UnityAssembliesCompat.GetLoadedAssemblies())
            {
                try
                {
                    if (assembly.IsDynamic) continue;
                    var location = assembly.Location;
                    if (string.IsNullOrEmpty(location)) continue;
                    if (!File.Exists(location)) continue;
                    paths.Add(location);
                }
                catch (NotSupportedException)
                {
                    // Some assemblies don't support Location property
                }
            }

            var result = new string[paths.Count];
            paths.CopyTo(result);
            return result;
        }

        private static string CheckBlockedPatterns(string code)
        {
            foreach (var pattern in _blockedPatterns)
            {
                if (code.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    return $"Code contains blocked pattern: '{pattern}'. Disable safety checks with safety_checks=false if this is intentional.";
            }
            return null;
        }

        private static void AddToHistory(string code, object result, double elapsedMs, bool safetyChecks, string compiler = "auto", string executionMode = "sync")
        {
            string preview;
            if (result is SuccessResponse sr)
                preview = sr.Data?.ToString() ?? sr.Message;
            else if (result is ErrorResponse er)
                preview = er.Error;
            else
                preview = result?.ToString() ?? "null";

            if (preview != null && preview.Length > 200)
                preview = preview.Substring(0, 200) + "...";

            _history.Add(new HistoryEntry
            {
                code = code,
                success = result is SuccessResponse,
                resultPreview = preview,
                elapsedMs = Math.Round(elapsedMs, 1),
                timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                safetyChecksEnabled = safetyChecks,
                compiler = compiler,
                executionMode = executionMode,
            });

            while (_history.Count > MaxHistoryEntries)
                _history.RemoveAt(0);
        }

        private static object SerializeResult(object result)
        {
            if (result == null) return null;

            var type = result.GetType();
            if (type.IsPrimitive || result is string || result is decimal)
                return result;

            try
            {
                return JToken.FromObject(result);
            }
            catch
            {
                return result.ToString();
            }
        }

        private class HistoryEntry
        {
            public string code;
            public bool success;
            public string resultPreview;
            public double elapsedMs;
            public string timestamp;
            public bool safetyChecksEnabled;
            public string compiler;
            public string executionMode;
        }
    }

    /// <summary>
    /// Roslyn compiler backend accessed entirely via reflection.
    /// No compile-time dependency on Microsoft.CodeAnalysis — works only if the package is installed.
    /// </summary>
    internal static class RoslynCompiler
    {
        private static bool? _isAvailable;
        private static Type _syntaxTreeType;
        private static Type _compilationType;
        private static Type _compilationOptionsType;
        private static Type _parseOptionsType;
        private static Type _metadataReferenceType;
        private static Type _outputKindEnum;
        private static Type _languageVersionEnum;
        private static MethodInfo _parseText;
        private static MethodInfo _createCompilation;
        private static MethodInfo _createFromFile;
        private static MethodInfo _emit;
        private static object _parseOptions;
        private static object _compilationOptions;

        public static bool IsAvailable
        {
            get
            {
                if (_isAvailable == null)
                    _isAvailable = Initialize();
                return _isAvailable.Value;
            }
        }

        public static void ResetCache()
        {
            _isAvailable = null;
        }

        private static bool Initialize()
        {
            try
            {
                _syntaxTreeType = Type.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree, Microsoft.CodeAnalysis.CSharp");
                _compilationType = Type.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation, Microsoft.CodeAnalysis.CSharp");
                _compilationOptionsType = Type.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions, Microsoft.CodeAnalysis.CSharp");
                _parseOptionsType = Type.GetType("Microsoft.CodeAnalysis.CSharp.CSharpParseOptions, Microsoft.CodeAnalysis.CSharp");
                _metadataReferenceType = Type.GetType("Microsoft.CodeAnalysis.MetadataReference, Microsoft.CodeAnalysis");
                _outputKindEnum = Type.GetType("Microsoft.CodeAnalysis.OutputKind, Microsoft.CodeAnalysis");
                _languageVersionEnum = Type.GetType("Microsoft.CodeAnalysis.CSharp.LanguageVersion, Microsoft.CodeAnalysis.CSharp");

                if (_syntaxTreeType == null || _compilationType == null || _compilationOptionsType == null ||
                    _parseOptionsType == null || _metadataReferenceType == null || _outputKindEnum == null ||
                    _languageVersionEnum == null)
                    return false;

                // CSharpSyntaxTree.ParseText(string, CSharpParseOptions, string, Encoding, CancellationToken)
                var syntaxTreeBase = Type.GetType("Microsoft.CodeAnalysis.SyntaxTree, Microsoft.CodeAnalysis");
                _parseText = _syntaxTreeType.GetMethod("ParseText", new[] { typeof(string), _parseOptionsType, typeof(string), typeof(Encoding), typeof(System.Threading.CancellationToken) });
                if (_parseText == null)
                    return false;

                // CSharpCompilation.Create(string, IEnumerable<SyntaxTree>, IEnumerable<MetadataReference>, CSharpCompilationOptions)
                var metadataRefBase = _metadataReferenceType;
                var syntaxTreeEnumerable = typeof(IEnumerable<>).MakeGenericType(syntaxTreeBase);
                var metadataRefEnumerable = typeof(IEnumerable<>).MakeGenericType(metadataRefBase);
                _createCompilation = _compilationType.GetMethod("Create", new[] { typeof(string), syntaxTreeEnumerable, metadataRefEnumerable, _compilationOptionsType });
                if (_createCompilation == null)
                    return false;

                // MetadataReference.CreateFromFile(string, MetadataReferenceProperties, DocumentationProvider)
                _createFromFile = _metadataReferenceType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "CreateFromFile");
                if (_createFromFile == null)
                    return false;

                // Emit has no single-param overload; the simplest is
                // Emit(Stream, Stream, Stream, Stream, IEnumerable<ResourceDescription>, EmitOptions, CancellationToken)
                var compilationBase = Type.GetType("Microsoft.CodeAnalysis.Compilation, Microsoft.CodeAnalysis");
                if (compilationBase == null) return false;
                _emit = compilationBase.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "Emit")
                    .OrderBy(m => m.GetParameters().Length)
                    .FirstOrDefault();
                if (_emit == null)
                    return false;

                // Build CSharpParseOptions — constructor has optional params, use reflection
                var latestValue = Enum.Parse(_languageVersionEnum, "Latest");
                var parseOptionsCtor = _parseOptionsType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)[0];
                var parseCtorParams = parseOptionsCtor.GetParameters();
                var parseArgs = new object[parseCtorParams.Length];
                for (int i = 0; i < parseCtorParams.Length; i++)
                {
                    if (parseCtorParams[i].Name == "languageVersion")
                        parseArgs[i] = latestValue;
                    else if (parseCtorParams[i].HasDefaultValue)
                        parseArgs[i] = parseCtorParams[i].DefaultValue;
                    else
                        parseArgs[i] = null;
                }
                _parseOptions = parseOptionsCtor.Invoke(parseArgs);

                // Build CSharpCompilationOptions — use the first constructor (has most defaults)
                var dllKind = Enum.Parse(_outputKindEnum, "DynamicallyLinkedLibrary");
                var compOptionsCtor = _compilationOptionsType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)[0];
                var compCtorParams = compOptionsCtor.GetParameters();
                var compArgs = new object[compCtorParams.Length];
                for (int i = 0; i < compCtorParams.Length; i++)
                {
                    if (compCtorParams[i].Name == "outputKind")
                        compArgs[i] = dllKind;
                    else if (compCtorParams[i].HasDefaultValue)
                        compArgs[i] = compCtorParams[i].DefaultValue;
                    else
                        compArgs[i] = null;
                }
                _compilationOptions = compOptionsCtor.Invoke(compArgs);

                return true;
            }
            catch (Exception e)
            {
                McpLog.Warn($"[ExecuteCode] Roslyn initialization failed: {e.Message}");
                return false;
            }
        }

        public static Assembly Compile(string source, string[] assemblyPaths, out List<string> errors)
        {
            errors = new List<string>();

            try
            {
                // Parse source
                var syntaxTree = _parseText.Invoke(null, new object[] { source, _parseOptions, null, null, default(System.Threading.CancellationToken) });

                // Build metadata references
                var metadataRefBase = _metadataReferenceType;
                var listType = typeof(List<>).MakeGenericType(metadataRefBase);
                var refs = (System.Collections.IList)Activator.CreateInstance(listType);

                foreach (var path in assemblyPaths)
                {
                    try
                    {
                        var cfParams = _createFromFile.GetParameters();
                        var cfArgs = new object[cfParams.Length];
                        cfArgs[0] = path; // string path
                        for (int i = 1; i < cfParams.Length; i++)
                            cfArgs[i] = cfParams[i].HasDefaultValue ? cfParams[i].DefaultValue : null;
                        var metaRef = _createFromFile.Invoke(null, cfArgs);
                        refs.Add(metaRef);
                    }
                    catch
                    {
                        // Skip assemblies that can't be loaded as metadata
                    }
                }

                // Build syntax tree array
                var syntaxTreeBase = Type.GetType("Microsoft.CodeAnalysis.SyntaxTree, Microsoft.CodeAnalysis");
                var treeArray = Array.CreateInstance(syntaxTreeBase, 1);
                treeArray.SetValue(syntaxTree, 0);

                // Create compilation
                var compilation = _createCompilation.Invoke(null, new object[] { "MCPDynamic", treeArray, refs, _compilationOptions });

                // Emit to memory
                using (var ms = new MemoryStream())
                {
                    // Build args for the Emit overload (fill non-stream params with defaults)
                    var emitParams = _emit.GetParameters();
                    var emitArgs = new object[emitParams.Length];
                    emitArgs[0] = ms; // peStream
                    for (int i = 1; i < emitParams.Length; i++)
                    {
                        if (emitParams[i].HasDefaultValue)
                            emitArgs[i] = emitParams[i].DefaultValue;
                        else
                            emitArgs[i] = null;
                    }
                    var emitResult = _emit.Invoke(compilation, emitArgs);

                    // Check emitResult.Success
                    var successProp = emitResult.GetType().GetProperty("Success");
                    bool success = (bool)successProp.GetValue(emitResult);

                    if (!success)
                    {
                        // Read emitResult.Diagnostics
                        var diagProp = emitResult.GetType().GetProperty("Diagnostics");
                        var diagnostics = (System.Collections.IEnumerable)diagProp.GetValue(emitResult);
                        var severityError = Enum.Parse(Type.GetType("Microsoft.CodeAnalysis.DiagnosticSeverity, Microsoft.CodeAnalysis"), "Error");

                        foreach (var diag in diagnostics)
                        {
                            var sevProp = diag.GetType().GetProperty("Severity");
                            var severity = sevProp.GetValue(diag);
                            if (!severity.Equals(severityError)) continue;

                            var locProp = diag.GetType().GetProperty("Location");
                            var loc = locProp.GetValue(diag);
                            var spanProp = loc.GetType().GetMethod("GetLineSpan");
                            var lineSpan = spanProp.Invoke(loc, null);
                            var startProp = lineSpan.GetType().GetProperty("StartLinePosition");
                            var startPos = startProp.GetValue(lineSpan);
                            var lineProp = startPos.GetType().GetProperty("Line");
                            int line = (int)lineProp.GetValue(startPos);

                            var msgProp = diag.GetType().GetMethod("GetMessage", new[] { typeof(System.Globalization.CultureInfo) });
                            string msg = (string)msgProp.Invoke(diag, new object[] { null });

                            int userLine = Math.Max(1, line + 1 - ExecuteCode.WrapperLineOffset);
                            errors.Add($"Line {userLine}: {msg}");
                        }
                        return null;
                    }

                    ms.Seek(0, SeekOrigin.Begin);
                    return Assembly.Load(ms.ToArray());
                }
            }
            catch (Exception e)
            {
                // Walk to the deepest cause: TargetInvocationException (and friends) wrap the real
                // failure inside .InnerException, and reporting only e.Message hides everything
                // useful (e.g. a missing transitive dep manifests as the generic "Exception has been
                // thrown by the target of an invocation.").
                Exception root = e;
                while (root.InnerException != null) root = root.InnerException;
                errors.Add($"Roslyn compilation error: {root.GetType().Name}: {root.Message}");
                return null;
            }
        }
    }
}
