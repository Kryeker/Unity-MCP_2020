using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// OS-level input injection via user32 SendInput (P/Invoke). Drives the legacy Input
    /// Manager like real hardware: the Unity window must be the foreground window during
    /// PlayMode. Used by ManageInput for the "device" path (gameplay input outside uGUI).
    ///
    /// Coordinate conversion: Unity screen space (bottom-left origin, pixels of the Game
    /// view) is mapped to OS screen space (top-left origin) through the GameView window
    /// rect and GUIUtility.GUIToScreenPoint.
    /// </summary>
    internal static class InputSendInput
    {
        // ── Win32 P/Invoke ────────────────────────────────────────────────────────────────────

        private const uint INPUT_MOUSE = 0;
        private const uint INPUT_KEYBOARD = 1;

        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        // ── Key name → VK mapping ─────────────────────────────────────────────────────────────

        private static readonly Dictionary<string, ushort> _keyMap =
            new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
            {
                { "space", 0x20 }, { "enter", 0x0D }, { "return", 0x0D }, { "escape", 0x1B },
                { "esc", 0x1B }, { "tab", 0x09 }, { "backspace", 0x08 }, { "delete", 0x2E },
                { "home", 0x24 }, { "end", 0x23 }, { "pageup", 0x21 }, { "pagedown", 0x22 },
                { "insert", 0x2D }, { "capslock", 0x14 },
                { "leftarrow", 0x25 }, { "uparrow", 0x26 }, { "rightarrow", 0x27 }, { "downarrow", 0x28 },
                { "left", 0x25 }, { "up", 0x26 }, { "right", 0x27 }, { "down", 0x28 },
                { "leftshift", 0xA0 }, { "rightshift", 0xA1 }, { "shift", 0xA0 },
                { "leftctrl", 0xA2 }, { "rightctrl", 0xA3 }, { "ctrl", 0xA2 }, { "leftcontrol", 0xA2 },
                { "leftalt", 0xA4 }, { "rightalt", 0xA5 }, { "alt", 0xA4 },
                { "leftmeta", 0x5B }, { "rightmeta", 0x5C },
                { "f1", 0x70 }, { "f2", 0x71 }, { "f3", 0x72 }, { "f4", 0x73 },
                { "f5", 0x74 }, { "f6", 0x75 }, { "f7", 0x76 }, { "f8", 0x77 },
                { "f9", 0x78 }, { "f10", 0x79 }, { "f11", 0x7A }, { "f12", 0x7B },
                { "semicolon", 0xBA }, { "equals", 0xBB }, { "comma", 0xBC }, { "minus", 0xBD },
                { "period", 0xBE }, { "slash", 0xBF }, { "backquote", 0xC0 },
                { "leftbracket", 0xDB }, { "backslash", 0xDC }, { "rightbracket", 0xDD }, { "quote", 0xDE },
            };

        static InputSendInput()
        {
            // Letters and digits map directly to VK codes.
            for (int i = 0; i < 26; i++)
                _keyMap[((char)('A' + i)).ToString()] = (ushort)(0x41 + i);
            for (int i = 0; i < 10; i++)
                _keyMap[i.ToString()] = (ushort)(0x30 + i);
        }

        public static bool TryResolveVk(string keyName, out ushort vk)
        {
            if (keyName != null && _keyMap.TryGetValue(keyName, out vk))
                return true;
            vk = 0;
            return false;
        }

        // ── Focused-input-field editing keys (IMGUI events for uGUI InputField) ───────────────

        private static readonly Dictionary<ushort, KeyCode> _vkToKeyCode =
            new Dictionary<ushort, KeyCode>
            {
                { 0x08, KeyCode.Backspace }, { 0x2E, KeyCode.Delete },
                { 0x0D, KeyCode.Return }, { 0x09, KeyCode.Tab }, { 0x1B, KeyCode.Escape },
                { 0x24, KeyCode.Home }, { 0x23, KeyCode.End },
                { 0x21, KeyCode.PageUp }, { 0x22, KeyCode.PageDown },
                { 0x25, KeyCode.LeftArrow }, { 0x26, KeyCode.UpArrow },
                { 0x27, KeyCode.RightArrow }, { 0x28, KeyCode.DownArrow },
            };

        public static bool IsEditingKey(ushort vk)
        {
            return _vkToKeyCode.ContainsKey(vk);
        }

        public static Event EditingKeyToEvent(ushort vk)
        {
            return new Event
            {
                type = EventType.KeyDown,
                keyCode = _vkToKeyCode.TryGetValue(vk, out var kc) ? kc : KeyCode.None,
            };
        }

        // ── Coordinate conversion ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Unity Game view screen position (bottom-left origin) → OS screen position
        /// (top-left origin, physical pixels). Returns false when the Game view cannot be
        /// resolved (e.g. not in PlayMode with a Game view open).
        /// </summary>
        private static bool UnityToOsScreen(Vector2 unityPos, out int osX, out int osY)
        {
            osX = 0;
            osY = 0;
            try
            {
                Type gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (gameViewType == null)
                    return false;
                EditorWindow gameView = EditorWindow.GetWindow(gameViewType, false, null, false);
                if (gameView == null)
                    return false;

                Rect pos = gameView.position;
                // Unity screen space is bottom-left; the window rect is top-left.
                Vector2 inWindow = new Vector2(
                    pos.x + unityPos.x,
                    pos.y + (pos.height - unityPos.y));
                Vector2 os = GUIUtility.GUIToScreenPoint(inWindow);
                osX = Mathf.RoundToInt(os.x);
                osY = Mathf.RoundToInt(os.y);
                return true;
            }
            catch (Exception e)
            {
                McpLog.Warn($"[ManageInput] GameView screen mapping failed: {e.Message}");
                return false;
            }
        }

        private static bool SendMouseEvent(uint flags, int dx, int dy, uint data = 0)
        {
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = dx,
                        dy = dy,
                        mouseData = data,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero,
                    },
                },
            };
            return SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT))) == 1;
        }

        private static bool SendKeyEvent(ushort vk, bool up, bool extended = false)
        {
            uint flags = up ? KEYEVENTF_KEYUP : 0;
            if (extended)
                flags |= KEYEVENTF_EXTENDEDKEY;
            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = 0,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero,
                    },
                },
            };
            return SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT))) == 1;
        }

        private static bool IsExtendedVk(ushort vk)
        {
            // Arrow keys, Delete, Home/End/PageUp/PageDown, Insert, right-side modifiers.
            return vk == 0x2E || vk == 0x2D || vk == 0x24 || vk == 0x23 || vk == 0x21
                || vk == 0x22 || vk == 0x25 || vk == 0x26 || vk == 0x27 || vk == 0x28
                || vk == 0xA1 || vk == 0xA3 || vk == 0xA5 || vk == 0x5C;
        }

        // ── Public API ─────────────────────────────────────────────────────────────────────────

        public static bool SendMouseMove(Vector2 unityPos)
        {
            if (!UnityToOsScreen(unityPos, out int x, out int y))
                return false;
            int sw = GetSystemMetrics(SM_CXSCREEN);
            int sh = GetSystemMetrics(SM_CYSCREEN);
            if (sw <= 0 || sh <= 0)
                return false;
            uint dx = (uint)(x * 65535 / Math.Max(1, sw - 1));
            uint dy = (uint)(y * 65535 / Math.Max(1, sh - 1));
            return SendMouseEvent(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, (int)dx, (int)dy);
        }

        public static bool SendMouseClick(Vector2 unityPos, int button)
        {
            if (!SendMouseMove(unityPos))
                return false;
            uint down = ButtonFlag(button, down: true);
            uint up = ButtonFlag(button, down: false);
            return SendMouseEvent(down, 0, 0) && SendMouseEvent(up, 0, 0);
        }

        public static bool SendMouseButton(Vector2? unityPos, int button, bool down)
        {
            if (unityPos.HasValue && !SendMouseMove(unityPos.Value))
                return false;
            return SendMouseEvent(ButtonFlag(button, down), 0, 0);
        }

        public static bool SendMouseDrag(Vector2 from, Vector2 to, int button)
        {
            if (!SendMouseMove(from))
                return false;
            uint down = ButtonFlag(button, down: true);
            uint up = ButtonFlag(button, down: false);
            if (!SendMouseEvent(down, 0, 0))
                return false;
            if (!SendMouseMove(to))
                return false;
            return SendMouseEvent(up, 0, 0);
        }

        public static bool SendMouseScroll(Vector2 unityPos, float scrollX, float scrollY)
        {
            if (!SendMouseMove(unityPos))
                return false;
            int wheel = Mathf.RoundToInt(scrollY * 120f); // one notch = 120
            if (wheel == 0 && scrollX != 0)
                wheel = Mathf.RoundToInt(scrollX * 120f);
            if (wheel == 0)
                return true; // zero scroll is a no-op
            return SendMouseEvent(MOUSEEVENTF_WHEEL, 0, 0, (uint)(ushort)wheel);
        }

        public static bool SendKeyPress(string keyName, ushort vk)
        {
            bool extended = IsExtendedVk(vk);
            return SendKeyEvent(vk, up: false, extended) && SendKeyEvent(vk, up: true, extended);
        }

        public static bool SendKeyDown(string keyName, ushort vk)
        {
            return SendKeyEvent(vk, up: false, IsExtendedVk(vk));
        }

        public static bool SendKeyUp(string keyName, ushort vk)
        {
            return SendKeyEvent(vk, up: true, IsExtendedVk(vk));
        }

        private static uint ButtonFlag(int button, bool down)
        {
            switch (button)
            {
                case 1: return down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
                case 2: return down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;
                default: return down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
            }
        }
    }
}
