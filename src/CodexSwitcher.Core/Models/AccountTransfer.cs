namespace CodexSwitcher.Core.Models;

/// <summary>Documento portátil usado para importar ou exportar uma ou mais contas do cofre.</summary>
public sealed class AccountTransferDocument
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public List<TransferredAccount> Accounts { get; set; } = [];
}

/// <summary>
/// Credencial exportada. O conteúdo é deliberadamente codificado em Base64, e não cifrado: o
/// arquivo foi feito para ser portável e deve ser tratado como uma senha.
/// </summary>
public sealed class TransferredAccount
{
    public string? Nickname { get; set; }
    public int SortOrder { get; set; }
    public string AuthJsonBase64 { get; set; } = string.Empty;
}
