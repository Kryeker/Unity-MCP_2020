using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Simulates mouse and keyboard input in PlayMode, dispatching across multiple editor
    /// frames so press/release states and drag interpolation are visible to game code.
    ///
    /// Two paths per positional action (mirroring Tuanjie's ManageInput):
    ///   UI path      – when the point raycasts onto a uGUI element, EventSystem handlers are
    ///                  invoked directly (pointerDown/Up/Click, hover enter/exit, dragHandler,
    ///                  scroll). Reliable for PlayMode UI; no input package required.
    ///   Device path  – otherwise drives gameplay input via OS-level SendInput (P/Invoke), which
    ///                  the legacy Input Manager reads like real hardware. Requires the Unity
    ///                  window to be the foreground window during PlayMode.
    ///
    /// Text entry uses the IMGUI event queue (InputField.ProcessEvent) — device injection never
    /// reaches uGUI text fields, matching Tuanjie's type_text design.
    ///
    /// Multi-frame sequencing (a "sequence" action) is a later iteration; single actions already
    /// span frames (press+release separated by a yield, drags interpolated at drag_speed px/s).
    /// </summary>
    [McpForUnityTool("manage_input", AutoRegister = false, Group = "core")]
    public static class ManageInput
    {
        private const string ActionClick = "mouse_click";
        private const string ActionMove = "mouse_move";
        private const string ActionDrag = "mouse_drag";
        private const string ActionScroll = "mouse_scroll";
        private const string ActionDown = "mouse_down";
        private const string ActionUp = "mouse_up";
        private const string ActionKeyPress = "key_press";
        private const string ActionKeyDown = "key_down";
        private const string ActionKeyUp = "key_up";
        private const string ActionTypeText = "type_text";

        private const float DefaultDragSpeed = 500f;

        // Input commands are serialized: they share static UI press/hover state.
        private static bool _isProcessing;

        // EventSystem press state bridging mouse_down to a later mouse_up.
        private static PointerEventData _uiPressData;
        private static GameObject _uiPressHandler;

        // Currently hovered UI object for pointerEnter/pointerExit dispatch.
        private static GameObject _uiHoverObject;

        // ── Multi-frame runner ────────────────────────────────────────────────────────────────

        private static IEnumerator _currentStep;
        private static Action<object> _setResult;

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void Tick()
        {
            if (_currentStep == null)
                return;
            bool done;
            try { done = !_currentStep.MoveNext(); }
            catch (Exception e)
            {
                McpLog.Error($"[ManageInput] Step threw: {e}");
                _setResult?.Invoke(new ErrorResponse($"Input simulation failed: {e.Message}"));
                _currentStep = null;
                _setResult = null;
                _isProcessing = false;
                return;
            }
            if (done)
            {
                _currentStep = null;
                _isProcessing = false;
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingPlayMode)
                return;
            // A step interrupted by play-mode exit is dropped without disposal; reset state so
            // input is not permanently wedged as busy.
            _currentStep = null;
            _setResult = null;
            _isProcessing = false;
            _uiPressData = null;
            _uiPressHandler = null;
            _uiHoverObject = null;
        }

        private static void RunStep(IEnumerator step, Action<object> setResult)
        {
            _currentStep = step;
            _setResult = setResult;
        }

        // ── Command entry ─────────────────────────────────────────────────────────────────────

        public static async Task<object> HandleCommand(JObject @params)
        {
            if (@params == null)
                return new ErrorResponse("Parameters cannot be null.");

            var p = new ToolParams(@params);
            var actionResult = p.GetRequired("action");
            if (!actionResult.IsSuccess)
                return new ErrorResponse(actionResult.ErrorMessage);
            string action = actionResult.Value.ToLowerInvariant();

            if (!EditorApplication.isPlaying)
            {
                return new ErrorResponse(
                    $"manage_input requires PlayMode. Enter Play mode before dispatching '{action}'.");
            }

            if (_isProcessing)
            {
                return new ErrorResponse(
                    "Another manage_input command is still running. Input commands must run one at a time.");
            }

            _isProcessing = true;
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            IEnumerator step = null;
            switch (action)
            {
                case ActionClick: step = MouseClickCoroutine(@params, r => tcs.TrySetResult(r)); break;
                case ActionMove: step = MouseMoveCoroutine(@params, r => tcs.TrySetResult(r)); break;
                case ActionDrag: step = MouseDragCoroutine(@params, r => tcs.TrySetResult(r)); break;
                case ActionScroll: step = MouseScrollCoroutine(@params, r => tcs.TrySetResult(r)); break;
                case ActionDown: step = MouseDownCoroutine(@params, r => tcs.TrySetResult(r)); break;
                case ActionUp: step = MouseUpCoroutine(@params, r => tcs.TrySetResult(r)); break;
                case ActionKeyPress: step = KeyPressCoroutine(@params, r => tcs.TrySetResult(r)); break;
                case ActionKeyDown: step = KeyDownCoroutine(@params, r => tcs.TrySetResult(r)); break;
                case ActionKeyUp: step = KeyUpCoroutine(@params, r => tcs.TrySetResult(r)); break;
                case ActionTypeText: step = TypeTextCoroutine(@params, r => tcs.TrySetResult(r)); break;
                default:
                    _isProcessing = false;
                    return new ErrorResponse(
                        $"Unknown action: '{action}'. Valid actions: {ActionClick}, {ActionMove}, {ActionDrag}, " +
                        $"{ActionScroll}, {ActionDown}, {ActionUp}, {ActionKeyPress}, {ActionKeyDown}, {ActionKeyUp}, {ActionTypeText}");
            }

            RunStep(step, r => tcs.TrySetResult(r));
            return await tcs.Task.ConfigureAwait(true);
        }

        // ── Shared helpers ────────────────────────────────────────────────────────────────────

        private static EventSystem GetEventSystem()
        {
            return EditorApplication.isPlaying ? EventSystem.current : null;
        }

        private static PointerEventData.InputButton ToInputButton(int button)
        {
            switch (button)
            {
                case 1: return PointerEventData.InputButton.Right;
                case 2: return PointerEventData.InputButton.Middle;
                default: return PointerEventData.InputButton.Left;
            }
        }

        private static bool TryRaycastUI(Vector2 screenPos, EventSystem es, out RaycastResult hit)
        {
            var pd = new PointerEventData(es) { position = screenPos };
            var results = new List<RaycastResult>();
            es.RaycastAll(pd, results);
            if (results.Count > 0)
            {
                hit = results[0];
                return true;
            }
            hit = default;
            return false;
        }

        private enum InputTarget { Auto, UI, World }

        private static InputTarget ParseTarget(JObject p)
        {
            switch (p["target"]?.ToString()?.ToLowerInvariant())
            {
                case "ui": return InputTarget.UI;
                case "world":
                case "device": return InputTarget.World;
                default: return InputTarget.Auto;
            }
        }

        private static void UpdateHover(EventSystem es, GameObject newHover, RaycastResult hit, Vector2 screenPos)
        {
            if (newHover == _uiHoverObject)
                return;
            var pd = new PointerEventData(es) { position = screenPos, pointerCurrentRaycast = hit };
            if (_uiHoverObject != null)
                ExecuteEvents.ExecuteHierarchy(_uiHoverObject, pd, ExecuteEvents.pointerExitHandler);
            if (newHover != null)
                ExecuteEvents.ExecuteHierarchy(newHover, pd, ExecuteEvents.pointerEnterHandler);
            _uiHoverObject = newHover;
        }

        // ── mouse_click ───────────────────────────────────────────────────────────────────────

        private static IEnumerator MouseClickCoroutine(JObject p, Action<object> setResult)
        {
            float x = p["x"]?.Value<float>() ?? 0f;
            float y = p["y"]?.Value<float>() ?? 0f;
            int button = p["button"]?.Value<int>() ?? 0;
            Vector2 screenPos = new Vector2(x, y);
            InputTarget target = ParseTarget(p);

            EventSystem es = GetEventSystem();
            if (target != InputTarget.World && es != null && TryRaycastUI(screenPos, es, out RaycastResult hit))
            {
                var go = hit.gameObject;
                var pd = new PointerEventData(es)
                {
                    position = screenPos,
                    pressPosition = screenPos,
                    button = ToInputButton(button),
                    pointerCurrentRaycast = hit,
                    pointerPressRaycast = hit,
                };

                var pressHandler = ExecuteEvents.ExecuteHierarchy(go, pd, ExecuteEvents.pointerDownHandler);
                if (pressHandler != null)
                {
                    pd.pointerPress = pressHandler;
                    pd.rawPointerPress = go;
                }

                yield return null; // one frame held

                if (pressHandler != null)
                    ExecuteEvents.Execute(pressHandler, pd, ExecuteEvents.pointerUpHandler);

                var clickHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(go);
                if (clickHandler != null)
                    ExecuteEvents.Execute(clickHandler, pd, ExecuteEvents.pointerClickHandler);

                setResult(new SuccessResponse(
                    $"Clicked UI element '{go.name}' at ({x:F1}, {y:F1})",
                    new { x, y, button, hitGameObject = go.name, method = "EventSystem" }));
                yield break;
            }

            if (target == InputTarget.UI)
            {
                setResult(new ErrorResponse($"target=ui but no UI element was hit at ({x:F1}, {y:F1})."));
                yield break;
            }

            if (!InputSendInput.SendMouseClick(screenPos, button))
            {
                setResult(new ErrorResponse(
                    "Device mouse click failed. The Unity window must be focused during PlayMode for SendInput; " +
                    "alternatively use target=ui for UI elements."));
                yield break;
            }

            setResult(new SuccessResponse(
                $"Mouse button {button} clicked at ({x:F1}, {y:F1})",
                new { x, y, button, method = "SendInput" }));
        }

        // ── mouse_move ────────────────────────────────────────────────────────────────────────

        private static IEnumerator MouseMoveCoroutine(JObject p, Action<object> setResult)
        {
            float x = p["x"]?.Value<float>() ?? 0f;
            float y = p["y"]?.Value<float>() ?? 0f;
            Vector2 screenPos = new Vector2(x, y);
            InputTarget target = ParseTarget(p);

            EventSystem es = GetEventSystem();
            if (target != InputTarget.World && es != null && TryRaycastUI(screenPos, es, out RaycastResult hit))
            {
                UpdateHover(es, hit.gameObject, hit, screenPos);
                setResult(new SuccessResponse(
                    $"Mouse hover updated at ({x:F1}, {y:F1})",
                    new { x, y, hitGameObject = hit.gameObject.name, method = "EventSystem" }));
                yield break;
            }

            if (target == InputTarget.UI)
            {
                setResult(new ErrorResponse($"target=ui but no UI element was hit at ({x:F1}, {y:F1})."));
                yield break;
            }

            if (es != null && _uiHoverObject != null)
                UpdateHover(es, null, default, screenPos);

            if (!InputSendInput.SendMouseMove(screenPos))
            {
                setResult(new ErrorResponse(
                    "Device mouse move failed. The Unity window must be focused during PlayMode for SendInput."));
                yield break;
            }
            setResult(new SuccessResponse(
                $"Mouse moved to ({x:F1}, {y:F1})", new { x, y, method = "SendInput" }));
        }

        // ── mouse_drag ────────────────────────────────────────────────────────────────────────

        private static IEnumerator MouseDragCoroutine(JObject p, Action<object> setResult)
        {
            float fromX = p["from_x"]?.Value<float>() ?? 0f;
            float fromY = p["from_y"]?.Value<float>() ?? 0f;
            float toX = p["x"]?.Value<float>() ?? 0f;
            float toY = p["y"]?.Value<float>() ?? 0f;
            int button = p["button"]?.Value<int>() ?? 0;
            float dragSpeed = p["drag_speed"]?.Value<float>() ?? DefaultDragSpeed;

            Vector2 screenFrom = new Vector2(fromX, fromY);
            Vector2 screenTo = new Vector2(toX, toY);
            InputTarget target = ParseTarget(p);

            EventSystem es = GetEventSystem();
            if (target != InputTarget.World && es != null && TryRaycastUI(screenFrom, es, out RaycastResult startHit))
            {
                var go = startHit.gameObject;
                var dragTarget = ExecuteEvents.GetEventHandler<IDragHandler>(go);
                if (dragTarget != null)
                {
                    var pd = new PointerEventData(es)
                    {
                        position = screenFrom,
                        pressPosition = screenFrom,
                        button = PointerEventData.InputButton.Left, // uGUI drags are left-button only
                        pointerCurrentRaycast = startHit,
                        pointerPressRaycast = startHit,
                        pointerDrag = dragTarget,
                    };

                    var pressHandler = ExecuteEvents.ExecuteHierarchy(go, pd, ExecuteEvents.pointerDownHandler);
                    if (pressHandler != null)
                        pd.pointerPress = pressHandler;

                    yield return null;

                    ExecuteEvents.Execute(dragTarget, pd, ExecuteEvents.beginDragHandler);
                    yield return null;

                    // Interpolate per frame so scroll views / sliders receive per-frame drag events.
                    Vector2 current = screenFrom;
                    float distance = Vector2.Distance(screenFrom, screenTo);
                    float steps = dragSpeed > 0 ? Mathf.Max(1f, distance / Mathf.Max(1f, dragSpeed) * 60f) : 1f;
                    for (int i = 1; i <= steps; i++)
                    {
                        current = Vector2.Lerp(screenFrom, screenTo, i / steps);
                        pd.position = current;
                        ExecuteEvents.Execute(dragTarget, pd, ExecuteEvents.dragHandler);
                        yield return null;
                    }

                    ExecuteEvents.Execute(dragTarget, pd, ExecuteEvents.endDragHandler);
                    yield return null;

                    if (pd.pointerPress != null)
                        ExecuteEvents.Execute(pd.pointerPress, pd, ExecuteEvents.pointerUpHandler);

                    setResult(new SuccessResponse(
                        $"Dragged '{go.name}' from ({fromX:F1}, {fromY:F1}) to ({toX:F1}, {toY:F1})",
                        new { fromX, fromY, x = toX, y = toY, hitGameObject = go.name, method = "EventSystem" }));
                    yield break;
                }

                if (target == InputTarget.UI)
                {
                    setResult(new ErrorResponse($"target=ui but no drag handler at ({fromX:F1}, {fromY:F1})."));
                    yield break;
                }
            }

            if (!InputSendInput.SendMouseDrag(screenFrom, screenTo, button))
            {
                setResult(new ErrorResponse(
                    "Device mouse drag failed. The Unity window must be focused during PlayMode for SendInput."));
                yield break;
            }
            setResult(new SuccessResponse(
                $"Dragged mouse from ({fromX:F1}, {fromY:F1}) to ({toX:F1}, {toY:F1})",
                new { fromX, fromY, x = toX, y = toY, method = "SendInput" }));
        }

        // ── mouse_scroll ──────────────────────────────────────────────────────────────────────

        private static IEnumerator MouseScrollCoroutine(JObject p, Action<object> setResult)
        {
            float x = p["x"]?.Value<float>() ?? 0f;
            float y = p["y"]?.Value<float>() ?? 0f;
            float scrollX = p["scroll_x"]?.Value<float>() ?? 0f;
            float scrollY = p["scroll_y"]?.Value<float>() ?? 0f;
            Vector2 screenPos = new Vector2(x, y);
            InputTarget target = ParseTarget(p);

            EventSystem es = GetEventSystem();
            if (target != InputTarget.World && es != null && TryRaycastUI(screenPos, es, out RaycastResult hit))
            {
                var go = hit.gameObject;
                var pd = new PointerEventData(es)
                {
                    position = screenPos,
                    pointerCurrentRaycast = hit,
                    scrollDelta = new Vector2(scrollX, scrollY),
                };
                ExecuteEvents.ExecuteHierarchy(go, pd, ExecuteEvents.scrollHandler);
                setResult(new SuccessResponse(
                    $"Scrolled at ({x:F1}, {y:F1}) by ({scrollX:F1}, {scrollY:F1})",
                    new { x, y, scrollX, scrollY, hitGameObject = go.name, method = "EventSystem" }));
                yield break;
            }

            if (target == InputTarget.UI)
            {
                setResult(new ErrorResponse($"target=ui but no UI element was hit at ({x:F1}, {y:F1})."));
                yield break;
            }

            if (!InputSendInput.SendMouseScroll(screenPos, scrollX, scrollY))
            {
                setResult(new ErrorResponse(
                    "Device mouse scroll failed. The Unity window must be focused during PlayMode for SendInput."));
                yield break;
            }
            setResult(new SuccessResponse(
                $"Scrolled at ({x:F1}, {y:F1}) by ({scrollX:F1}, {scrollY:F1})",
                new { x, y, scrollX, scrollY, method = "SendInput" }));
        }

        // ── mouse_down / mouse_up ─────────────────────────────────────────────────────────────

        private static IEnumerator MouseDownCoroutine(JObject p, Action<object> setResult)
        {
            float x = p["x"]?.Value<float>() ?? 0f;
            float y = p["y"]?.Value<float>() ?? 0f;
            int button = p["button"]?.Value<int>() ?? 0;
            Vector2 screenPos = new Vector2(x, y);
            InputTarget target = ParseTarget(p);

            EventSystem es = GetEventSystem();
            if (target != InputTarget.World && es != null && TryRaycastUI(screenPos, es, out RaycastResult hit))
            {
                var go = hit.gameObject;
                var pd = new PointerEventData(es)
                {
                    position = screenPos,
                    pressPosition = screenPos,
                    button = ToInputButton(button),
                    pointerCurrentRaycast = hit,
                    pointerPressRaycast = hit,
                };
                var pressHandler = ExecuteEvents.ExecuteHierarchy(go, pd, ExecuteEvents.pointerDownHandler);
                if (pressHandler != null)
                {
                    pd.pointerPress = pressHandler;
                    pd.rawPointerPress = go;
                }
                _uiPressData = pd;
                _uiPressHandler = pressHandler;
                setResult(new SuccessResponse(
                    $"Mouse down on '{go.name}' at ({x:F1}, {y:F1})",
                    new { x, y, button, hitGameObject = go.name, method = "EventSystem" }));
                yield break;
            }

            if (target == InputTarget.UI)
            {
                setResult(new ErrorResponse($"target=ui but no UI element was hit at ({x:F1}, {y:F1})."));
                yield break;
            }

            if (!InputSendInput.SendMouseButton(screenPos, button, down: true))
            {
                setResult(new ErrorResponse(
                    "Device mouse down failed. The Unity window must be focused during PlayMode for SendInput."));
                yield break;
            }
            setResult(new SuccessResponse(
                $"Mouse button {button} down at ({x:F1}, {y:F1})", new { x, y, button, method = "SendInput" }));
        }

        private static IEnumerator MouseUpCoroutine(JObject p, Action<object> setResult)
        {
            int button = p["button"]?.Value<int>() ?? 0;

            if (_uiPressData != null && _uiPressHandler != null)
            {
                var pd = _uiPressData;
                ExecuteEvents.Execute(_uiPressHandler, pd, ExecuteEvents.pointerUpHandler);
                var clickHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(pd.pointerPress);
                if (clickHandler != null)
                    ExecuteEvents.Execute(clickHandler, pd, ExecuteEvents.pointerClickHandler);
                _uiPressData = null;
                _uiPressHandler = null;
                setResult(new SuccessResponse(
                    $"Mouse up released UI press on '{pd.pointerPress?.name}'",
                    new { button, method = "EventSystem" }));
                yield break;
            }

            if (!InputSendInput.SendMouseButton(null, button, down: false))
            {
                setResult(new ErrorResponse(
                    "Device mouse up failed. The Unity window must be focused during PlayMode for SendInput."));
                yield break;
            }
            setResult(new SuccessResponse($"Mouse button {button} up", new { button, method = "SendInput" }));
        }

        // ── Keyboard ──────────────────────────────────────────────────────────────────────────

        private static IEnumerator KeyPressCoroutine(JObject p, Action<object> setResult)
        {
            string keyName = p["key"]?.ToString();
            if (string.IsNullOrEmpty(keyName))
            {
                setResult(new ErrorResponse(
                    "'key' parameter is required. Examples: \"W\", \"Space\", \"Enter\", \"LeftShift\", \"A\"."));
                yield break;
            }

            if (!InputSendInput.TryResolveVk(keyName, out ushort vk))
            {
                setResult(new ErrorResponse(
                    $"Invalid key name: \"{keyName}\". Examples: \"W\", \"Space\", \"Enter\", \"LeftShift\", \"A\", \"LeftArrow\"."));
                yield break;
            }

            // Focused-input-field path: editing/navigation keys go to the uGUI/TMP field through
            // the IMGUI event queue — device injection never reaches text entry.
            if (InputSendInput.IsEditingKey(vk))
            {
                var sink = ResolveFocusedTextField();
                if (sink.Valid)
                {
                    var ev = InputSendInput.EditingKeyToEvent(vk);
                    sink.Process(ev);
                    sink.Finish();
                    yield return null;
                    setResult(new SuccessResponse(
                        $"Sent editing key '{keyName}' to input field '{sink.Name}'",
                        new { key = keyName, field = sink.Name, method = "EventSystem" }));
                    yield break;
                }
            }

            if (!InputSendInput.SendKeyPress(keyName, vk))
            {
                setResult(new ErrorResponse(
                    "Device key press failed. The Unity window must be focused during PlayMode for SendInput."));
                yield break;
            }
            setResult(new SuccessResponse($"Key '{keyName}' pressed", new { key = keyName, method = "SendInput" }));
        }

        private static IEnumerator KeyDownCoroutine(JObject p, Action<object> setResult)
        {
            string keyName = p["key"]?.ToString();
            if (string.IsNullOrEmpty(keyName) || !InputSendInput.TryResolveVk(keyName, out ushort vk))
            {
                setResult(new ErrorResponse(
                    $"'key' parameter is required and must be a known key name (e.g. \"W\", \"Space\"). Got: \"{keyName}\"."));
                yield break;
            }
            if (!InputSendInput.SendKeyDown(keyName, vk))
            {
                setResult(new ErrorResponse(
                    "Device key down failed. The Unity window must be focused during PlayMode for SendInput."));
                yield break;
            }
            setResult(new SuccessResponse($"Key '{keyName}' down", new { key = keyName, method = "SendInput" }));
        }

        private static IEnumerator KeyUpCoroutine(JObject p, Action<object> setResult)
        {
            string keyName = p["key"]?.ToString();
            if (string.IsNullOrEmpty(keyName) || !InputSendInput.TryResolveVk(keyName, out ushort vk))
            {
                setResult(new ErrorResponse(
                    $"'key' parameter is required and must be a known key name (e.g. \"W\", \"Space\"). Got: \"{keyName}\"."));
                yield break;
            }
            if (!InputSendInput.SendKeyUp(keyName, vk))
            {
                setResult(new ErrorResponse(
                    "Device key up failed. The Unity window must be focused during PlayMode for SendInput."));
                yield break;
            }
            setResult(new SuccessResponse($"Key '{keyName}' up", new { key = keyName, method = "SendInput" }));
        }

        // ── type_text ─────────────────────────────────────────────────────────────────────────

        private static IEnumerator TypeTextCoroutine(JObject p, Action<object> setResult)
        {
            string text = p["text"]?.ToString();
            if (string.IsNullOrEmpty(text))
            {
                setResult(new ErrorResponse("'text' parameter is required and must be a non-empty string."));
                yield break;
            }

            var sink = ResolveFocusedTextField();
            if (!sink.Valid)
            {
                setResult(new ErrorResponse(
                    "type_text needs a focused uGUI InputField or TMP_InputField. Select one first " +
                    "(e.g. mouse_click on it) so it becomes EventSystem.currentSelectedGameObject."));
                yield break;
            }

            foreach (char c in text)
                sink.Process(NewCharEvent(c));
            sink.Finish();

            yield return null; // let the label / onValueChanged settle a frame

            setResult(new SuccessResponse(
                $"Typed {text.Length} character(s) into '{sink.Name}'",
                new { text, characters = text.Length, field = sink.Name, method = "EventSystem" }));
        }

        // ── Focused text-field dispatch (uGUI direct, TMP via reflection) ────────────────────

        private struct TextInputSink
        {
            public string Name;
            public Action<Event> Process;
            public Action Finish;
            public bool Valid => Process != null;
        }

        private static bool _tmpTypeResolved;
        private static Type _tmpInputFieldType;

        private static TextInputSink ResolveFocusedTextField()
        {
            var sink = new TextInputSink();

            EventSystem es = GetEventSystem();
            GameObject sel = es != null ? es.currentSelectedGameObject : null;
            if (sel == null)
                return sink;

            var uiField = sel.GetComponent<UnityEngine.UI.InputField>();
            if (uiField != null)
            {
                sink.Name = sel.name;
                sink.Process = e => uiField.ProcessEvent(e);
                sink.Finish = () => uiField.ForceLabelUpdate();
                return sink;
            }

            if (!_tmpTypeResolved)
            {
                _tmpInputFieldType = Type.GetType("TMPro.TMP_InputField, Unity.TextMeshPro");
                _tmpTypeResolved = true;
            }
            if (_tmpInputFieldType != null)
            {
                Component tmp = sel.GetComponent(_tmpInputFieldType);
                if (tmp != null)
                {
                    var process = _tmpInputFieldType.GetMethod("ProcessEvent", new[] { typeof(Event) });
                    var finish = _tmpInputFieldType.GetMethod("ForceLabelUpdate", Type.EmptyTypes);
                    if (process != null)
                    {
                        sink.Name = sel.name;
                        sink.Process = e => process.Invoke(tmp, new object[] { e });
                        sink.Finish = () => finish?.Invoke(tmp, null);
                    }
                }
            }
            return sink;
        }

        private static Event NewCharEvent(char c)
        {
            return new Event { type = EventType.KeyDown, character = c };
        }
    }
}
