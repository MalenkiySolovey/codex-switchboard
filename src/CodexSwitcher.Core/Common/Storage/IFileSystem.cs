using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Security.Totp;
using CodexSwitcher.Core.Security.Verification;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Core.Transfer.Contracts;
using CodexSwitcher.Core.Transfer.Models;
using CodexSwitcher.Core.Transfer.Services;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
namespace CodexSwitcher.Core.Common.Storage;

/// <summary>
/// Abstração de sistema de arquivos com <b>escrita atômica</b>, para permitir testar as
/// transações de switch/refresh sem tocar o disco real. Ver BUSINESS_RULES.md §2.1 e ponto 10.
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);

    byte[] ReadAllBytes(string path);
    string ReadAllText(string path);

    /// <summary>Escrita atômica: grava em arquivo temporário no mesmo diretório e faz move-replace.</summary>
    void WriteAllBytesAtomic(string path, byte[] contents);

    /// <summary>Escrita atômica de texto (UTF-8 sem BOM).</summary>
    void WriteAllTextAtomic(string path, string contents);

    void Copy(string sourcePath, string destPath, bool overwrite);
    void Move(string sourcePath, string destPath, bool overwrite);
    void Delete(string path);

    IReadOnlyList<string> EnumerateFiles(string directory, string searchPattern);
}
