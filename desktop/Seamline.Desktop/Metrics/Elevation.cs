using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace Seamline.Desktop.Metrics;

/// <summary>FPS e temperatura da CPU só funcionam com o Seamline aberto como administrador.</summary>
internal static class Elevation
{
    /// <summary>Argumento que abre o painel gamer assim que o Seamline inicia.</summary>
    public const string OpenPanelArgument = "--painel-gamer";

    public static bool IsAdmin => Environment.IsPrivilegedProcess;

    /// <summary>
    /// Abre o Seamline de novo como administrador (o Windows pergunta) e fecha este.
    /// Retorna false se o usuário disse "Não" na pergunta do Windows.
    /// </summary>
    public static bool RestartAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, OpenPanelArgument)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Win32Exception)
        {
            return false; // o usuário recusou
        }
        foreach (var window in Application.Current.Windows.Cast<Window>().ToList())
            window.Close();
        Application.Current.Shutdown();
        return true;
    }
}
