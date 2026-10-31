using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Seamline.Desktop;

/// <summary>
/// Deixa a barra de título da janela escura (o WPF sozinho sempre desenha a barra clara do Windows).
/// Usado pelo estilo SeamlineWindow do tema: <c>local:WindowTheme.DarkTitleBar="True"</c>.
/// </summary>
public static class WindowTheme
{
    public static readonly DependencyProperty DarkTitleBarProperty = DependencyProperty.RegisterAttached(
        "DarkTitleBar", typeof(bool), typeof(WindowTheme), new PropertyMetadata(false, OnDarkTitleBarChanged));

    public static bool GetDarkTitleBar(DependencyObject element) => (bool)element.GetValue(DarkTitleBarProperty);
    public static void SetDarkTitleBar(DependencyObject element, bool value) => element.SetValue(DarkTitleBarProperty, value);

    private static void OnDarkTitleBarChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Window window || e.NewValue is not true)
            return;
        // A janela do Windows (HWND) só existe depois do SourceInitialized; antes de aparecer na tela.
        if (new WindowInteropHelper(window).Handle != 0)
            Apply(window);
        else
            window.SourceInitialized += (_, _) => Apply(window);
    }

    private static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        // No Windows 11 dá para pintar a barra com a mesma cor do fundo da janela (no 10 é ignorado).
        if (window.Background is SolidColorBrush { Color: var color })
        {
            var colorRef = color.R | color.G << 8 | color.B << 16;
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));
        }
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
