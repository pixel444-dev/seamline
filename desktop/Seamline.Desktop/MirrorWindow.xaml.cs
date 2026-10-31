using System.Windows;
using Seamline.Desktop.Input;
using Seamline.Desktop.Video;

namespace Seamline.Desktop;

/// <summary>Mostra a tela inteira do celular e repassa cliques e teclas (Fase 1).</summary>
public partial class MirrorWindow : Window
{
    public MirrorWindow(DeviceSession session, Action<string> log)
    {
        InitializeComponent();
        Title = $"Seamline — {session.Info.Model}";

        var video = new VideoPresenter(Screen, session.Video, session.VideoWidth, session.VideoHeight, log);
        video.Ended += Close;
        Closed += (_, _) => video.Dispose();
        _ = new ScreenInput(this, Screen, session, appWindow: null);

        // Tamanho inicial: 85% da altura útil da tela do PC, mantendo a proporção do celular.
        var scale = Math.Min(1.0, SystemParameters.WorkArea.Height * 0.85 / session.VideoHeight);
        Screen.Width = session.VideoWidth * scale;
        Screen.Height = session.VideoHeight * scale;
        ContentRendered += (_, _) =>
        {
            // Depois de abrir, a janela pode ser redimensionada à vontade.
            SizeToContent = SizeToContent.Manual;
            Screen.Width = double.NaN;
            Screen.Height = double.NaN;
        };
    }
}
