using CodexSwitcher.Core.Models;

namespace CodexSwitcher.App.Services;

/// <summary>
/// Abstrai as interações que precisam da UI (diálogos, janela de login), para o ViewModel
/// permanecer testável e sem dependência direta de XAML. Ver BUSINESS_RULES.md §4.2 (confirmação).
/// </summary>
public interface IUiInteraction
{
    /// <summary>Popup de confirmação do switch: lista o que será fechado/reaberto. Ver §4.2.</summary>
    Task<bool> ConfirmSwitchAsync(SwitchPlan plan);

    /// <summary>Confirmação genérica (ex.: remover conta). Retorna true se confirmado.</summary>
    Task<bool> ConfirmAsync(string title, string message, string okText, bool destructive = false);

    /// <summary>Pede um texto (ex.: apelido). Retorna null se cancelado.</summary>
    Task<string?> PromptTextAsync(string title, string prompt, string initialValue, string okText);

    /// <summary>Mostra uma mensagem simples.</summary>
    Task ShowMessageAsync(string title, string message);

    /// <summary>
    /// Abre o login efêmero (WebView2 descartável) e retorna o auth.json capturado, ou null se
    /// cancelado/falhou. Ver §5.
    /// </summary>
    Task<byte[]?> RunEphemeralLoginAsync();

    /// <summary>Escolhe um arquivo exportado pelo Switcher ou um auth.json do Codex e devolve seu conteúdo.</summary>
    Task<string?> PickImportFileAsync();

    /// <summary>Escolhe onde salvar um arquivo de contas e grava seu conteúdo.</summary>
    Task<bool> SaveExportFileAsync(string suggestedFileName, string contents);
}
