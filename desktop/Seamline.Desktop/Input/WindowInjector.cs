using System.Runtime.InteropServices;
using static Seamline.Desktop.Input.WindowNative;

namespace Seamline.Desktop.Input;

/// <summary>
/// Fase 5: repassa toques, mouse e teclas que vêm do celular para uma janela do PC que está "no
/// celular" (escondida fora da tela). Não dá para usar o mouse de verdade (ele mexeria no cursor do
/// usuário e a janela nem está visível), então mandamos as mensagens de mouse e teclado direto para
/// a janela (PostMessage).
///
/// Limitação conhecida: funciona bem com programas "clássicos" do Windows (Bloco de Notas antigo,
/// Explorador, a maioria dos programas Win32 e muitos baseados em Chromium), mas alguns programas
/// ignoram mensagens que não vêm do mouse de verdade (apps da Loja/UWP, jogos, alguns botões).
/// </summary>
internal sealed class WindowInjector(IntPtr window)
{
    private IntPtr _captured; // janela que recebeu o "botão apertado": o arraste continua nela
    private bool _leftDown;

    /// <summary>
    /// <paramref name="action"/>: down, move, up, hover, scroll, right_down, right_up.
    /// <paramref name="x"/>/<paramref name="y"/> de 0 a 1 dentro da imagem da janela.
    /// </summary>
    public void Pointer(string action, double x, double y, double scrollX = 0, double scrollY = 0)
    {
        if (!TryGetBounds(out var bounds))
            return;
        var screen = new POINT(
            bounds.Left + (int)Math.Round(x * (bounds.Width - 1)),
            bounds.Top + (int)Math.Round(y * (bounds.Height - 1)));

        var target = _captured != 0 && action is "move" or "up" ? _captured : ChildAt(screen);
        var hit = HitTest(target, screen);
        if (hit != HTCLIENT && _captured == 0)
        {
            // Barra de título: só o botão de fechar é tratado (arrastar a janela não faz sentido aqui).
            if (action == "up" && hit == HTCLOSE)
                PostMessage(window, WM_SYSCOMMAND, SC_CLOSE, 0);
            return;
        }

        var client = screen;
        ScreenToClient(target, ref client);
        var lParam = MakeLParam(client.X, client.Y);
        switch (action)
        {
            case "down":
                _captured = target;
                _leftDown = true;
                PostMessage(target, WM_MOUSEMOVE, 0, lParam);
                PostMessage(target, WM_LBUTTONDOWN, MK_LBUTTON, lParam);
                break;
            case "move":
                PostMessage(target, WM_MOUSEMOVE, _leftDown ? MK_LBUTTON : 0, lParam);
                break;
            case "hover":
                PostMessage(target, WM_MOUSEMOVE, 0, lParam);
                break;
            case "up":
                PostMessage(target, WM_LBUTTONUP, 0, lParam);
                _captured = 0;
                _leftDown = false;
                break;
            case "right_down":
                PostMessage(target, WM_RBUTTONDOWN, MK_RBUTTON, lParam);
                break;
            case "right_up":
                PostMessage(target, WM_RBUTTONUP, 0, lParam);
                PostMessage(target, WM_CONTEXTMENU, target, MakeLParam(screen.X, screen.Y));
                break;
            case "scroll":
                // A roda usa coordenadas de tela. 1 "clique" do Android = 120 no Windows.
                var screenParam = MakeLParam(screen.X, screen.Y);
                if (scrollY != 0)
                    PostMessage(target, WM_MOUSEWHEEL, MakeWParam(0, (int)Math.Round(scrollY * 120)), screenParam);
                if (scrollX != 0)
                    PostMessage(target, WM_MOUSEHWHEEL, MakeWParam(0, (int)Math.Round(scrollX * 120)), screenParam);
                break;
        }
    }

    /// <summary>
    /// Uma tecla do celular. Letras e símbolos vão como caractere (WM_CHAR), já com o layout do
    /// celular aplicado; Enter, setas, Backspace etc. vão como tecla.
    /// </summary>
    public void Key(bool down, int androidKeyCode, int character, bool ctrl, bool alt)
    {
        var target = FocusedChild();
        var virtualKey = AndroidKeys.ToVirtualKey(androidKeyCode);
        if (character != 0 && !ctrl && !alt && virtualKey is not (VK_RETURN or VK_TAB or VK_BACK))
        {
            if (down)
                PostMessage(target, WM_CHAR, character, 1);
            return;
        }
        if (virtualKey == 0)
            return;
        var scan = MapVirtualKey((uint)virtualKey, 0);
        var lParam = (IntPtr)(1 | (scan << 16) | (down ? 0 : 0xC0000000));
        // Enter, Tab e Backspace: o próprio programa transforma a tecla em caractere (TranslateMessage).
        PostMessage(target, down ? WM_KEYDOWN : WM_KEYUP, virtualKey, lParam);
    }

    /// <summary>Retângulo da janela como aparece na captura (sem as bordas invisíveis do Windows).</summary>
    public bool TryGetBounds(out RECT bounds) =>
        DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out bounds, Marshal.SizeOf<RECT>()) == 0
        || GetWindowRect(window, out bounds);

    /// <summary>A janela-filha mais funda embaixo do ponto (botão, caixa de texto...).</summary>
    private IntPtr ChildAt(POINT screen)
    {
        var current = window;
        while (true)
        {
            var client = screen;
            ScreenToClient(current, ref client);
            var child = ChildWindowFromPointEx(current, client, CWP_SKIPINVISIBLE | CWP_SKIPDISABLED | CWP_SKIPTRANSPARENT);
            if (child == 0 || child == current)
                return current;
            current = child;
        }
    }

    private static int HitTest(IntPtr target, POINT screen)
    {
        var ok = SendMessageTimeout(target, WM_NCHITTEST, 0, MakeLParam(screen.X, screen.Y), SMTO_ABORTIFHUNG, 200, out var result);
        return ok == 0 ? HTCLIENT : (int)result;
    }

    /// <summary>O controle com o foco do teclado dentro da janela (mesmo ela não estando em primeiro plano).</summary>
    private IntPtr FocusedChild()
    {
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        var thread = GetWindowThreadProcessId(window, out _);
        if (GetGUIThreadInfo(thread, ref info) && info.hwndFocus != 0)
            return info.hwndFocus;
        return _captured != 0 ? _captured : window;
    }

    private static IntPtr MakeLParam(int low, int high) => (IntPtr)((high << 16) | (low & 0xFFFF));
    private static IntPtr MakeWParam(int low, int high) => (IntPtr)((high << 16) | (low & 0xFFFF));
}

/// <summary>Teclas do Android (KeyEvent.KEYCODE_*) → teclas virtuais do Windows (VK_*).</summary>
internal static class AndroidKeys
{
    public static int ToVirtualKey(int code) => code switch
    {
        >= 29 and <= 54 => 'A' + (code - 29), // A–Z
        >= 7 and <= 16 => '0' + (code - 7), // 0–9
        >= 131 and <= 142 => 0x70 + (code - 131), // F1–F12
        66 or 160 => WindowNative.VK_RETURN, // Enter, Enter do teclado numérico
        67 => WindowNative.VK_BACK, // Backspace
        112 => 0x2E, // Delete
        61 => WindowNative.VK_TAB,
        111 => 0x1B, // Esc
        62 => 0x20, // espaço
        19 => 0x26, // cima
        20 => 0x28, // baixo
        21 => 0x25, // esquerda
        22 => 0x27, // direita
        122 => 0x24, // Home
        123 => 0x23, // End
        92 => 0x21, // Page Up
        93 => 0x22, // Page Down
        124 => 0x2D, // Insert
        _ => 0,
    };
}

/// <summary>Funções do Windows usadas para mandar mensagens a outras janelas.</summary>
internal static class WindowNative
{
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_CHAR = 0x0102;
    public const int WM_SYSCOMMAND = 0x0112;
    public const int WM_CONTEXTMENU = 0x007B;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_MOUSEHWHEEL = 0x020E;
    public const int MK_LBUTTON = 0x0001;
    public const int MK_RBUTTON = 0x0002;
    public const int SC_CLOSE = 0xF060;
    public const int HTCLIENT = 1;
    public const int HTCLOSE = 20;
    public const int VK_BACK = 0x08;
    public const int VK_TAB = 0x09;
    public const int VK_RETURN = 0x0D;
    public const uint CWP_SKIPINVISIBLE = 0x1;
    public const uint CWP_SKIPDISABLED = 0x2;
    public const uint CWP_SKIPTRANSPARENT = 0x4;
    public const uint SMTO_ABORTIFHUNG = 0x2;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int SW_RESTORE = 9;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    public static extern IntPtr ChildWindowFromPointEx(IntPtr hwnd, POINT pt, uint flags);

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_TRANSPARENT = 0x00000020;
    public const long WS_EX_LAYERED = 0x00080000;
    public const uint LWA_ALPHA = 0x2;
    public const uint GW_OWNER = 4;
    public const int DWMWA_CLOAKED = 14;
    public const uint RDW_INVALIDATE = 0x1, RDW_ALLCHILDREN = 0x80, RDW_FRAME = 0x400;

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

    [DllImport("user32.dll")]
    public static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    [DllImport("user32.dll")]
    public static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, char[] text, int max);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    public static string GetTitle(IntPtr hwnd)
    {
        var buffer = new char[256];
        var length = GetWindowText(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(0, length));
    }
}
