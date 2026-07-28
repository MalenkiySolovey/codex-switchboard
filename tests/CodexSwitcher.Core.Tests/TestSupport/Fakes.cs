using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Tests.TestSupport;

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
}

public sealed class FakeAudit : IAuditLog
{
    public List<(string Action, string Outcome, string? Detail)> Entries { get; } = [];
    public void Record(string action, string outcome, string? detail = null) =>
        Entries.Add((action, outcome, detail));
}

public sealed class FakeConfigStore : ICodexConfigStore
{
    public CredentialsStoreKind Current { get; set; } = CredentialsStoreKind.Unset;
    public bool EnsureCalled { get; private set; }

    public CredentialsStoreKind ReadCredentialsStore(string configTomlPath) => Current;

    public bool EnsureFileStore(string configTomlPath)
    {
        EnsureCalled = true;
        var changed = Current != CredentialsStoreKind.File;
        Current = CredentialsStoreKind.File;
        return changed;
    }
}

public sealed class FakeProcessManager : IProcessManager
{
    public List<CodexProcessInfo> Running { get; set; } = [];
    public bool RemnantAfterClose { get; set; }
    public bool ThrowOnRelaunch { get; set; }
    public bool CloseCalled { get; private set; }
    public List<CodexProcessInfo> Relaunched { get; } = [];
    private bool _closed;

    public IReadOnlyList<CodexProcessInfo> FindRunningCodexProcesses() => Running;

    public bool AnyCodexCliRunning() =>
        _closed ? RemnantAfterClose : Running.Any(p => p.Kind == CodexProcessKind.Cli);

    public Task<IReadOnlyList<CodexProcessInfo>> CloseGracefullyThenKillAsync(
        IReadOnlyList<CodexProcessInfo> targets, TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
    {
        CloseCalled = true;
        _closed = true;
        return Task.FromResult(targets);
    }

    public void Relaunch(CodexProcessInfo process)
    {
        if (ThrowOnRelaunch)
            throw new IOException("relaunch failed (injected)");
        Relaunched.Add(process);
    }
}

public sealed class FakeCodexCli : ICodexCli
{
    public bool IsAvailable { get; set; } = true;
    public string? ResolvedPath { get; set; } = @"C:\npm\codex.exe";

    public string? LastCodexHome { get; private set; }

    public Task<ICodexLoginSession> StartChatGptLoginAsync(string codexHome,
        CancellationToken cancellationToken = default)
    {
        LastCodexHome = codexHome;
        return Task.FromResult<ICodexLoginSession>(new FakeLoginSession());
    }

}

public sealed class FakeLoginSession : ICodexLoginSession
{
    public string AuthUrl { get; set; } = "https://auth.openai.com/oauth/authorize?fake=1";
    public string LoginId { get; set; } = "fake-login-id";
    public Task<CodexLoginResult> Completion { get; set; } =
        Task.FromResult(new CodexLoginResult(true));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Decora um IFileSystem real para injetar falha na escrita atômica de um caminho específico.</summary>
public sealed class FaultInjectingFileSystem : IFileSystem
{
    private readonly IFileSystem _inner;
    public string? FailAtomicWriteForPath { get; set; }

    public FaultInjectingFileSystem(IFileSystem inner) => _inner = inner;

    public void WriteAllBytesAtomic(string path, byte[] contents)
    {
        if (FailAtomicWriteForPath is not null && SamePath(path, FailAtomicWriteForPath))
            throw new IOException("injected write failure");
        _inner.WriteAllBytesAtomic(path, contents);
    }

    public void WriteAllTextAtomic(string path, string contents)
    {
        if (FailAtomicWriteForPath is not null && SamePath(path, FailAtomicWriteForPath))
            throw new IOException("injected write failure");
        _inner.WriteAllTextAtomic(path, contents);
    }

    public bool FileExists(string path) => _inner.FileExists(path);
    public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
    public void CreateDirectory(string path) => _inner.CreateDirectory(path);
    public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);
    public string ReadAllText(string path) => _inner.ReadAllText(path);
    public void Copy(string s, string d, bool o) => _inner.Copy(s, d, o);
    public void Move(string s, string d, bool o) => _inner.Move(s, d, o);
    public void Delete(string path) => _inner.Delete(path);
    public IReadOnlyList<string> EnumerateFiles(string dir, string pattern) => _inner.EnumerateFiles(dir, pattern);

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
