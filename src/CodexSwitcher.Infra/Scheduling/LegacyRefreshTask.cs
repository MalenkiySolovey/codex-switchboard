using System.Diagnostics;

namespace CodexSwitcher.Infra.Scheduling;

/// <summary>Remove a tarefa de renovação criada por versões anteriores do aplicativo.</summary>
public static class LegacyRefreshTask
{
    private const string TaskName = "CodexSwitcherRefresh";

    public static void Remove()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Delete /TN {TaskName} /F",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(5_000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Melhor esforço: a tarefa não afeta as novas versões mesmo se não puder ser removida agora.
        }
    }
}
