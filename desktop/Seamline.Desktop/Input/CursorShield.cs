using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static Seamline.Desktop.Input.Native;

namespace Seamline.Desktop.Input;

/// <summary>
/// Esconde o cursor do Windows enquanto ele está "no celular". O cursor fica parado no meio do
/// monitor, em cima desta janelinha quase transparente que pede "sem cursor". Ela fica sempre por
/// cima, não aparece na barra de tarefas e não rouba o foco do programa que estava aberto.
/// </summary>
internal sealed class CursorShield : Window
{
    private const int SizePx = 160;

    public CursorShield()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // 100% transparente não recebe o mouse
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Cursor = Cursors.None;
        Width = Height = 1;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
    }

    /// <summary>Mostra a janela centrada em <paramref name="x"/>, <paramref name="y"/> (pixels físicos da tela).</summary>
    public void ShowAt(int x, int y)
    {
        Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, HWND_TOPMOST, x - SizePx / 2, y - SizePx / 2, SizePx, SizePx, SWP_NOACTIVATE);
        // Faz o Windows reavaliar qual cursor mostrar agora que a janela está embaixo dele.
        SetCursorPos(x, y);
    }
}
