namespace CodexSwitcher.Infra.Io;

/// <summary>
/// Limpeza das pastas efêmeras de perfis descartáveis (WebView2 <c>codex-webview-*</c> do login,
/// CODEX_HOME <c>codex-login-*</c> do login, e <c>codex-browser-*</c> de cada aba do navegador
/// privado) sob o TempRoot. O WebView2 grava cache/cookies/histórico no seu perfil isolado; apagamos
/// a pasta ao fechar, mas handles ainda presos ou arquivos read-only do plugin-clone do app-server
/// (.git) podem impedir a exclusão. Este helper força a exclusão (limpa atributos read-only) e varre
/// resíduos de sessões anteriores na inicialização.
/// </summary>
public static class TempCleanup
{
    private static readonly string[] EphemeralPrefixes = ["codex-webview-", "codex-login-", "codex-browser-", "usage-"];

    /// <summary>
    /// Apaga, na inicialização, quaisquer pastas efêmeras que sobraram de execuções anteriores.
    /// Best-effort: nenhuma sessão de login/navegador está ativa no arranque, então é seguro varrer tudo.
    /// </summary>
    public static void SweepLoginTemp(string tempRoot)
    {
        if (!Directory.Exists(tempRoot)) return;

        foreach (var dir in EnumerateDirs(tempRoot))
        {
            var name = Path.GetFileName(dir);
            if (EphemeralPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                TryForceDelete(dir);
        }
    }

    /// <summary>Apaga a pasta recursivamente, limpando atributos read-only. Retorna true se sumiu.</summary>
    public static bool TryForceDelete(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return true;
            ClearReadOnlyAttributes(dir);
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ClearReadOnlyAttributes(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static IEnumerable<string> EnumerateDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
