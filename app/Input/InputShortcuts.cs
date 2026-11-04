using System;
using System.Linq;
using Windows.Gaming.Input;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Nyx.Input
{
    /// <summary>
    /// Shortcuts on top of the system's mouse mode. On Xbox a XAML app already gets a gamepad-driven cursor
    /// (A = click, sticks = move / scroll, B = back), and a plugged-in USB mouse and keyboard work natively in WebView2.
    ///
    /// Gamepad: LB / RB previous / next tab, Y address bar, X protection panel, Menu main menu, View new tab.
    /// Keyboard: Ctrl+L / F6 address bar, Ctrl+D bookmark, Ctrl+T new tab, Ctrl+W close tab, Ctrl+Tab / Ctrl+Shift+Tab switch tabs,
    ///           F5 / Ctrl+R reload, Alt+Left / Alt+Right back / forward.
    /// </summary>
    public sealed class InputShortcuts : IDisposable
    {
        public event Action FocusUrl, Reload, Back, Forward, NextTab, PrevTab, NewTab, CloseTab, OpenShield, OpenMenu, ToggleBookmark, SuggestUp, SuggestDown, ReopenTab;

        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        private GamepadButtons _prev;

        public InputShortcuts()
        {
            _timer.Tick += (s, e) => Poll();
            _timer.Start();
            Window.Current.CoreWindow.KeyDown += OnKeyDown;
        }

        private void Poll()
        {
            var pad = Gamepad.Gamepads.FirstOrDefault();
            if (pad == null) { _prev = GamepadButtons.None; return; }
            var now = pad.GetCurrentReading().Buttons;
            var pressed = now & ~_prev;
            _prev = now;
            if (pressed == GamepadButtons.None) return;

            if ((pressed & GamepadButtons.Y) != 0) FocusUrl?.Invoke();
            if ((pressed & GamepadButtons.X) != 0) OpenShield?.Invoke();
            if ((pressed & GamepadButtons.LeftShoulder) != 0) PrevTab?.Invoke();
            if ((pressed & GamepadButtons.RightShoulder) != 0) NextTab?.Invoke();
            if ((pressed & GamepadButtons.Menu) != 0) OpenMenu?.Invoke();
            if ((pressed & GamepadButtons.View) != 0) NewTab?.Invoke();
            if ((pressed & GamepadButtons.DPadDown) != 0) SuggestDown?.Invoke();
            if ((pressed & GamepadButtons.DPadUp) != 0) SuggestUp?.Invoke();
        }

        private static bool Down(VirtualKey k) =>
            (Window.Current.CoreWindow.GetKeyState(k) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

        private void OnKeyDown(CoreWindow sender, KeyEventArgs e)
        {
            bool ctrl = Down(VirtualKey.Control), alt = Down(VirtualKey.Menu), shift = Down(VirtualKey.Shift);
            switch (e.VirtualKey)
            {
                case VirtualKey.L when ctrl: FocusUrl?.Invoke(); e.Handled = true; break;
                case VirtualKey.F6: FocusUrl?.Invoke(); e.Handled = true; break;
                case VirtualKey.T when ctrl && shift: ReopenTab?.Invoke(); e.Handled = true; break;
                case VirtualKey.T when ctrl: NewTab?.Invoke(); e.Handled = true; break;
                case VirtualKey.D when ctrl: ToggleBookmark?.Invoke(); e.Handled = true; break;
                case VirtualKey.W when ctrl: CloseTab?.Invoke(); e.Handled = true; break;
                case VirtualKey.Tab when ctrl && shift: PrevTab?.Invoke(); e.Handled = true; break;
                case VirtualKey.Tab when ctrl: NextTab?.Invoke(); e.Handled = true; break;
                case VirtualKey.F5: Reload?.Invoke(); e.Handled = true; break;
                case VirtualKey.R when ctrl: Reload?.Invoke(); e.Handled = true; break;
                case VirtualKey.Left when alt: Back?.Invoke(); e.Handled = true; break;
                case VirtualKey.Right when alt: Forward?.Invoke(); e.Handled = true; break;
            }
        }

        public void Dispose()
        {
            _timer.Stop();
            Window.Current.CoreWindow.KeyDown -= OnKeyDown;
        }
    }
}
