using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Seamline.Desktop.Metrics;

namespace Seamline.Desktop;

/// <summary>
/// Painel gamer: FPS, uso e temperatura da CPU e da GPU, RAM e VRAM, atualizados a cada segundo.
/// O mesmo painel pode aparecer no celular (<see cref="ShowOnPhone"/>).
/// </summary>
public partial class GamerPanelWindow : Window
{
    private IDisposable? _subscription;

    /// <summary>Abre o painel no celular. Retorna o texto para mostrar na linha de situação.</summary>
    public Func<Task<string>>? ShowOnPhone { get; set; }

    public GamerPanelWindow()
    {
        InitializeComponent();
        AdminButton.Visibility = Elevation.IsAdmin ? Visibility.Collapsed : Visibility.Visible;
        Loaded += (_, _) => _subscription = MetricsCollector.Shared.Subscribe(m => Dispatcher.BeginInvoke(() => Show(m)));
        Closed += (_, _) => _subscription?.Dispose();
    }

    private void Show(PcMetrics m)
    {
        StatusLine.Text = Elevation.IsAdmin
            ? "Atualiza a cada segundo · aberto como administrador"
            : "Atualiza a cada segundo";

        // FPS
        var hasGame = m.Fps is > 0;
        FpsText.Text = hasGame ? $"{m.Fps:0}" : "--";
        FpsText.Opacity = hasGame ? 1 : 0.35;
        FrameTimeText.Text = hasGame ? $"{m.FrameMs:0.0} ms" : "--";
        LowText.Text = hasGame ? $"{m.FpsLow1:0}" : "--";
        GameText.Text = m.Game;
        FpsHint.Text = MetricTexts.FpsHint(m) ?? "";
        if (m.FpsStatus == "ok")
            FpsChart.Push(m.Fps ?? 0);

        // CPU
        CpuNameText.Text = m.CpuName;
        CpuGauge.AnimateTo(m.CpuUsage);
        CpuUsageText.Text = $"{m.CpuUsage:0}%";
        SetTemperature(CpuTempText, m.CpuTemp);
        CpuClockText.Text = m.CpuClockMhz is { } clock ? $"{clock / 1000:0.00} GHz" : "--";
        CpuTempHint.Text = MetricTexts.CpuTempHint(m.CpuTempStatus) ?? "";

        // GPU
        GpuNameText.Text = m.GpuName;
        GpuGauge.AnimateTo(m.GpuUsage ?? 0);
        GpuUsageText.Text = m.GpuUsage is { } gpu ? $"{gpu:0}%" : "--";
        SetTemperature(GpuTempText, m.GpuTemp);
        GpuFanText.Text = m.GpuFanRpm is { } fan ? (fan > 0 ? $"{fan:0} rpm" : "parada") : "--";
        GpuFanText.ToolTip = m.GpuFanRpm is 0 ? "A placa desliga a ventoinha quando está fria (modo silencioso)" : null;
        GpuPowerText.Text = m.GpuPower is { } power ? $"Consumo: {power:0}% do máximo" : "";

        // Memórias
        RamText.Text = $"{m.RamUsedMb / 1024:0.0} / {m.RamTotalMb / 1024:0.0} GB";
        AnimateBar(RamBar, m.RamUsedMb / m.RamTotalMb);
        if (m.VramUsedMb is { } vram && m.VramTotalMb > 0)
        {
            VramText.Text = $"{vram / 1024:0.0} / {m.VramTotalMb / 1024:0.0} GB";
            AnimateBar(VramBar, vram / m.VramTotalMb);
        }
    }

    private static void SetTemperature(System.Windows.Controls.TextBlock text, double? celsius)
    {
        text.Text = celsius is { } c ? $"{c:0}°C" : "--";
        text.Foreground = celsius is { } t ? new SolidColorBrush(HeatColors.ForTemperature(t)) : Brushes.Gray;
    }

    private static void AnimateBar(FrameworkElement bar, double fraction)
    {
        var scale = (System.Windows.Media.ScaleTransform)bar.RenderTransform;
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new DoubleAnimation(Math.Clamp(fraction, 0, 1), TimeSpan.FromMilliseconds(600)) { EasingFunction = new CubicEase() });
    }

    private void AdminButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "O Windows só deixa ver o FPS dos jogos e a temperatura do processador para programas abertos como administrador.\n\n" +
                "O Seamline vai fechar e abrir de novo como administrador (o Windows vai perguntar). " +
                "Se o celular estiver conectado, conecte de novo depois.\n\nContinuar?",
                "Ativar FPS e temperatura da CPU", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        if (!Elevation.RestartAsAdmin())
            StatusLine.Text = "Tudo bem: sem administrador, o FPS e a temperatura da CPU ficam desligados.";
    }

    private async void PhoneButton_Click(object sender, RoutedEventArgs e)
    {
        if (ShowOnPhone is null)
            return;
        PhoneButton.IsEnabled = false;
        try
        {
            StatusLine.Text = "Abrindo o painel no celular...";
            StatusLine.Text = await ShowOnPhone();
        }
        finally
        {
            PhoneButton.IsEnabled = true;
        }
    }
}
