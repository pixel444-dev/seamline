namespace Seamline.Desktop.Metrics;

/// <summary>Explicações curtas quando uma métrica não aparece (o app do celular tem as mesmas).</summary>
public static class MetricTexts
{
    public static string? FpsHint(PcMetrics m) => m.FpsStatus switch
    {
        "admin" => "Para ver o FPS, clique em \"Ativar FPS e temp. da CPU\" lá em cima.",
        "error" => "Não consegui ligar a contagem de FPS do Windows.",
        _ when m.Fps is not > 0 => "Abra um jogo (DirectX 9 a 12) para ver o FPS.",
        _ => null,
    };

    public static string? CpuTempHint(string status) => status switch
    {
        "admin" => "Temperatura: precisa abrir como administrador.",
        "pawnio" => "Temperatura: instale o driver gratuito PawnIO (pawnio.eu) e abra o Seamline de novo.",
        "none" => "Este processador não informou a temperatura.",
        _ => null,
    };
}
