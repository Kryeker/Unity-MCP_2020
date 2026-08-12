using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using UnityEditor;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Wait sessions behind wait_for_idle / wait_for_compile, driven by
    /// <see cref="WaitSessionRunner"/> across domain reloads. Design follows the Tuanjie
    /// Codely bridge's StepJobs: a small state machine that advances one step per editor
    /// frame, with every field persisted so the session resumes after a reload.
    /// </summary>
    public static class WaitSessions
    {
        [UnityEditor.InitializeOnLoadMethod]
        private static void RegisterFactories()
        {
            WaitSessionRunner.RegisterKind(WaitForIdleSession.KindName, () => new WaitForIdleSession());
            WaitSessionRunner.RegisterKind(WaitForCompileSession.KindName, () => new WaitForCompileSession());
        }
    }

    /// <summary>
    /// Waits until every other in-flight bridge job — test run, package install, execute_code
    /// coroutine, asset-generation job — has finished, and the editor is neither compiling nor
    /// importing. Jobs enrolled under <see cref="KindName"/> are excluded from the wait, so
    /// concurrent wait_for_idle sessions do not hold each other in flight forever.
    /// </summary>
    internal class WaitForIdleSession : WaitSessionRunner.WaitSession
    {
        public const string KindName = "wait_for_idle";

        public override string Kind => KindName;

        public override bool Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (StartedAt <= 0)
                StartedAt = now;

            if (JobRegistry.Count(excludeName: KindName) == 0
                && !CompilationHelper.IsCompiling()
                && !EditorApplication.isUpdating
                && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                CompleteAsSuccess("Editor is idle: no jobs in flight, not compiling, not importing.",
                    new
                    {
                        pendingJobs = 0,
                        isCompiling = false,
                        isUpdating = false,
                        elapsed_ms = Math.Round(now - StartedAt, 1),
                    });
                return true;
            }
            return false;
        }

        public override void HandleTimeout()
        {
            TimedOut = true;
            var jobs = JobRegistry.Snapshot();
            string detail = jobs.Count > 0
                ? " In-flight: " + string.Join(", ", jobs.Select(j => $"{j.Name} ({j.Runner})"))
                : string.Empty;
            CompleteAsError($"Timed out waiting for the editor to become idle.{detail}");
        }
    }

    /// <summary>
    /// Waits for a compilation session to run to completion, then reports its outcome.
    ///
    /// Three phases, mirroring Tuanjie's WaitForCompileJob:
    ///   detect — look for compilation to start, giving up after a settle window that only
    ///            counts down against an idle editor (an import/refresh in flight is what a
    ///            compile comes out of, and on a large project it runs for hundreds of frames,
    ///            so a naive frame count would expire long before the compile begins).
    ///   await  — wait for compiling/importing to end.
    ///   report — read console error/warning counts (valid across the reload) and finish.
    ///
    /// <see cref="SettleSeconds"/> is short for a bare wait ("is the editor compiling right
    /// now?" — an idle editor answers the honest "no" without stalling) and raised by the
    /// wait_for_compile tool when it just requested a compile and therefore knows one is coming.
    /// </summary>
    internal class WaitForCompileSession : WaitSessionRunner.WaitSession
    {
        public const string KindName = "wait_for_compile";

        public override string Kind => KindName;

        public string Phase = "detect";
        public double SettleUntil;
        public bool SawCompiling;
        public double CompileStartedAt;
        /// <summary>True once a compilation was requested for this session (persisted; only the first invocation triggers).</summary>
        public bool CompileTriggered;
        /// <summary>Detect-window length in seconds; 2 for a bare wait, 30 after a trigger.</summary>
        public double SettleSeconds = 2;

        public override bool Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (StartedAt <= 0)
                StartedAt = now;

            switch (Phase)
            {
                case "detect":
                    // The settle window only counts down against an idle editor: push it
                    // forward while an import/refresh is in flight.
                    if (SettleUntil <= 0)
                        SettleUntil = now + SettleSeconds;
                    else if (EditorApplication.isUpdating)
                        SettleUntil = now + SettleSeconds;

                    if (CompilationHelper.IsCompiling())
                    {
                        SawCompiling = true;
                        if (CompileStartedAt <= 0)
                            CompileStartedAt = now;
                    }

                    if (SawCompiling || now >= SettleUntil)
                        Phase = "await";
                    return false;

                case "await":
                    if (!CompilationHelper.IsCompiling() && !EditorApplication.isUpdating)
                        Phase = "report";
                    return false;

                case "report":
                    BuildResult();
                    return true;

                default:
                    Phase = "report";
                    return false;
            }
        }

        public override void HandleTimeout()
        {
            TimedOut = true;
            CompleteAsError($"Timed out waiting for compilation (phase '{Phase}').");
        }

        private void BuildResult()
        {
            var errors = CompilationHelper.GetCompilationErrors();
            var warnings = CompilationHelper.GetCompilationWarnings();

            var data = new Dictionary<string, object>
            {
                ["status"] = "completed",
                ["pipeline_kind"] = "compile",
                // False means compilation never started while we watched. The counts below are
                // then the previous build's, so the caller must not read them as "this edit is clean".
                ["requires_console_validation"] = true,
                ["compile_observed"] = SawCompiling,
                ["elapsed_ms"] = Math.Round(EditorApplication.timeSinceStartup - StartedAt, 1),
            };
            if (errors.HasValue)
            {
                data["errors"] = errors.Value;
                data["hasErrors"] = errors.Value > 0;
            }
            if (warnings.HasValue)
            {
                data["warnings"] = warnings.Value;
                data["hasWarnings"] = warnings.Value > 0;
            }

            string message;
            if (!SawCompiling)
            {
                message = "No compilation was observed — the scripts were already up to date, or " +
                          "the editor never started one. Any error/warning counts below are from " +
                          "the previous build; validate via console.";
            }
            else
            {
                message = errors.HasValue
                    ? (errors.Value > 0 ? "Compilation completed with errors" : "Compilation completed successfully")
                    : "Compilation completed (validate via console)";
            }

            CompleteAsSuccess(message, data);
        }
    }
}
