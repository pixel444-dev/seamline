using System.ComponentModel;
using System.Runtime.InteropServices;
using static Seamline.Desktop.Input.Native;

namespace Seamline.Desktop.Input;

/// <summary>
/// Vê todo o mouse e o teclado do PC, em qualquer programa, e pode "segurar" um evento para ele
/// não chegar ao Windows (é assim que o cursor fica parado enquanto controla o celular).
///
/// - Ganchos globais (WH_MOUSE_LL e WH_KEYBOARD_LL): avisam cada evento e permitem bloqueá-lo.
/// - Raw Input: o movimento "cru" do mouse, sem a aceleração do Windows e sem parar na borda da tela.
///
/// Tudo roda numa thread própria com fila de mensagens. Se rodasse na thread da interface, qualquer
/// travadinha da janela deixaria o mouse do PC inteiro lento (o Windows espera o gancho responder).
/// Por isso os tratadores precisam ser rápidos e não podem esperar a rede.
/// </summary>
internal sealed class LowLevelInput : IDisposable
{
    /// <summary>Recebe a mensagem (WM_MOUSEMOVE...) e os dados; retorna true para bloquear o evento.</summary>
    public delegate bool MouseHandler(int message, in MSLLHOOKSTRUCT data);

    /// <summary>Recebe os dados da tecla; retorna true para bloquear o evento.</summary>
    public delegate bool KeyboardHandler(in KBDLLHOOKSTRUCT data);

    private readonly MouseHandler _onMouse;
    private readonly KeyboardHandler _onKeyboard;
    private readonly Action<int, int> _onRawMove;

    // Os delegates precisam continuar vivos enquanto o Windows guarda o ponteiro para eles.
    private readonly HookProc _mouseProc;
    private readonly HookProc _keyboardProc;
    private readonly WndProc _wndProc;

    private readonly Thread _thread;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint _threadId;

    public LowLevelInput(MouseHandler onMouse, KeyboardHandler onKeyboard, Action<int, int> onRawMove)
    {
        _onMouse = onMouse;
        _onKeyboard = onKeyboard;
        _onRawMove = onRawMove;
        _mouseProc = MouseProc;
        _keyboardProc = KeyboardProc;
        _wndProc = WindowProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "Ganchos de mouse e teclado" };
        _thread.Start();
        _started.Task.GetAwaiter().GetResult(); // repassa o erro se não conseguiu instalar os ganchos
    }

    private void Run()
    {
        IntPtr mouseHook = 0, keyboardHook = 0, window = 0;
        try
        {
            _threadId = GetCurrentThreadId();
            var module = GetModuleHandle(null);
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
            keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
            if (mouseHook == 0 || keyboardHook == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Não consegui instalar os ganchos de mouse/teclado.");
            window = CreateRawInputWindow(module);
            _started.SetResult();
        }
        catch (Exception e)
        {
            Cleanup();
            _started.TrySetException(e);
            return;
        }

        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(msg);
            DispatchMessage(msg);
        }
        Cleanup();

        void Cleanup()
        {
            if (mouseHook != 0) UnhookWindowsHookEx(mouseHook);
            if (keyboardHook != 0) UnhookWindowsHookEx(keyboardHook);
            if (window != 0) DestroyWindow(window);
        }
    }

    /// <summary>Uma janela invisível "só de mensagens" que recebe o Raw Input do mouse, mesmo sem foco.</summary>
    private IntPtr CreateRawInputWindow(IntPtr module)
    {
        var className = $"SeamlineRawInput{Environment.ProcessId}";
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = module,
            lpszClassName = className,
        };
        RegisterClassEx(wc); // se já existir (segunda conexão), tudo bem
        var window = CreateWindowEx(0, className, null, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, module, 0);
        if (window == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Não consegui criar a janela do Raw Input.");

        var device = new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = window };
        if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Não consegui registrar o Raw Input do mouse.");
        return window;
    }

    private unsafe IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Guard(() => _onMouse((int)wParam, in *(MSLLHOOKSTRUCT*)lParam)))
            return 1;
        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    private unsafe IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Guard(() => _onKeyboard(in *(KBDLLHOOKSTRUCT*)lParam)))
            return 1;
        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    private unsafe IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
        {
            var headerSize = (uint)sizeof(RAWINPUTHEADER);
            const int bufferSize = 64;
            var buffer = stackalloc byte[bufferSize];
            var size = (uint)bufferSize;
            if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, headerSize) != unchecked((uint)-1)
                && ((RAWINPUTHEADER*)buffer)->dwType == RIM_TYPEMOUSE)
            {
                var mouse = (RAWMOUSE*)(buffer + headerSize);
                // Mesas digitalizadoras e áreas remotas mandam posição absoluta: ignoramos (o gancho cobre).
                if ((mouse->usFlags & MOUSE_MOVE_ABSOLUTE) == 0 && (mouse->lLastX != 0 || mouse->lLastY != 0))
                {
                    var dx = mouse->lLastX;
                    var dy = mouse->lLastY;
                    Guard(() => { _onRawMove(dx, dy); return false; });
                }
            }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Um erro aqui dentro não pode derrubar o app nem deixar o mouse travado.</summary>
    private static bool Guard(Func<bool> action)
    {
        try
        {
            return action();
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"Erro no gancho de entrada: {e}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_QUIT, 0, 0);
        _thread.Join(1000);
    }
}
