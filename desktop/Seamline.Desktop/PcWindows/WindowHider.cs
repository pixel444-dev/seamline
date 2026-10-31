using Seamline.Desktop.Input;
using static Seamline.Desktop.Input.WindowNative;

namespace Seamline.Desktop.PcWindows;

/// <summary>
/// Esconde uma janela do PC enquanto ela está no celular, sem parar o desenho dela.
///
/// Testado no Windows 11: fora da tela (mesmo 1 pixel para fora) o Windows deixa de redesenhar a
/// janela e a captura congela; "camuflar" (DWM cloak) só funciona nas janelas do próprio programa.
/// O que funciona em qualquer janela: deixar no lugar, 100% transparente e "atravessável" pelo mouse
/// (WS_EX_LAYERED + WS_EX_TRANSPARENT). A captura continua vendo a imagem normal.
/// </summary>
internal sealed class WindowHider(IntPtr window)
{
    // Janelas escondidas agora: se o Seamline fechar (ou travar), elas reaparecem.
    private static readonly HashSet<WindowHider> s_hidden = [];

    static WindowHider()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ShowAll();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => ShowAll();
    }

    private IntPtr _oldExStyle;
    private bool _wasLayered;
    private uint _oldKey;
    private byte _oldAlpha;
    private uint _oldFlags;
    private bool _hidden;

    /// <summary>Esconde a janela. Retorna false se não deu (ex.: janela com transparência própria).</summary>
    public bool Hide()
    {
        if (_hidden)
            return true;
        // Minimizada a janela não é desenhada: volta ao tamanho normal (sem roubar o foco).
        if (IsIconic(window))
            ShowWindow(window, SW_SHOWNOACTIVATE);

        _oldExStyle = GetWindowLongPtr(window, GWL_EXSTYLE);
        _wasLayered = (_oldExStyle & WS_EX_LAYERED) != 0;
        if (_wasLayered && !GetLayeredWindowAttributes(window, out _oldKey, out _oldAlpha, out _oldFlags))
            return false; // desenha a própria transparência (UpdateLayeredWindow): não mexemos

        SetWindowLongPtr(window, GWL_EXSTYLE, (IntPtr)((long)_oldExStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT));
        SetLayeredWindowAttributes(window, 0, 0, LWA_ALPHA);
        _hidden = true;
        lock (s_hidden)
            s_hidden.Add(this);
        return true;
    }

    public void Show()
    {
        if (!_hidden)
            return;
        _hidden = false;
        lock (s_hidden)
            s_hidden.Remove(this);
        if (!IsWindow(window))
            return;
        if (_wasLayered)
            SetLayeredWindowAttributes(window, _oldKey, _oldAlpha, _oldFlags);
        SetWindowLongPtr(window, GWL_EXSTYLE, _oldExStyle);
        RedrawWindow(window, 0, 0, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_FRAME);
    }

    private static void ShowAll()
    {
        WindowHider[] all;
        lock (s_hidden)
            all = [.. s_hidden];
        foreach (var hider in all)
            hider.Show();
    }
}
