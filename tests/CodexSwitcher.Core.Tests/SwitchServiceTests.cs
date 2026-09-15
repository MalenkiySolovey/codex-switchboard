using CodexSwitcher.Core.Tests.TestSupport;

namespace CodexSwitcher.Core.Tests;

/// <summary>
/// Testa a transação de switch com foco em <b>nunca perder um login</b>: write-back do slot ativo,
/// backup, escrita atômica e rollback. Ver BUSINESS_RULES.md §4 e pontos 2, 3, 6, 24.
/// </summary>
public sealed class SwitchServiceTests
{
    private sealed class Env : IDisposable
    {
        public TempDir Dir { get; } = new();
        public FaultInjectingFileSystem Fs { get; }
        public VaultService Vault { get; }
        public ProfileStore Store { get; }
        public FakeProcessManager Proc { get; } = new();
        public FakeConfigStore Config { get; } = new();
        public FakeClock Clock { get; } = new();
        public FakeAudit Audit { get; } = new();
        public CodexPaths Paths { get; }
        public string BackupsDir { get; }
        public SwitchService Service { get; }
        public List<ProfileMetadata> Profiles { get; } = [];

        public Env()
        {
            Fs = new FaultInjectingFileSystem(new PhysicalFileSystem());
            Vault = new VaultService(new DpapiSecretProtector(), Fs, Dir.Combine("vault"));
            Store = new ProfileStore(Fs, Dir.Combine("profiles.json"));
            Paths = CodexPaths.ForHome(Dir.Combine(".codex"));
            BackupsDir = Dir.Combine("backups");
            Service = new SwitchService(Vault, Store, Fs, Proc, Config, Clock, Audit, Paths, BackupsDir);
        }

        public ProfileMetadata AddProfile(string nick, byte[] authBytes, bool active)
        {
            var p = new ProfileMetadata
            {
                Nickname = nick,
                CreatedAt = Clock.UtcNow,
                IsActive = active,
                HealthStatus = HealthStatus.Valid,
            };
            p.BlobFingerprint = Vault.SaveBlob(p.Id, authBytes);
            Profiles.Add(p);
            return p;
        }

        public void SetActiveSlot(byte[] bytes) => Fs.WriteAllBytesAtomic(Paths.ActiveAuthPath, bytes);
        public byte[] ReadActiveSlot() => Fs.ReadAllBytes(Paths.ActiveAuthPath);

        public void Dispose() => Dir.Dispose();
    }

    private static SwitchExecutionOptions AutoOpts =>
        new(CloseReopenMode.Automatic, TimeSpan.FromSeconds(1), BackupsToKeep: 10);

    private static CodexProcessInfo DesktopApp(int pid = 100) =>
        new(pid, "Codex", @"C:\Apps\Codex\Codex.exe", null, CodexProcessKind.DesktopApp);

    private static CodexProcessInfo Cli(int pid = 200) =>
        new(pid, "codex", @"C:\npm\codex.exe", "exec", CodexProcessKind.Cli);

    [Fact]
    public async Task HappyPath_WritesTargetToSlot_AndUpdatesMetadata()
    {
        using var env = new Env();
        var aBytes = Sample.AuthJson(accountId: "acct_A", refreshToken: "rt-A");
        var bBytes = Sample.AuthJson(accountId: "acct_B", refreshToken: "rt-B");
        var a = env.AddProfile("A", aBytes, active: true);
        var b = env.AddProfile("B", bBytes, active: false);
        env.SetActiveSlot(aBytes);

        var result = await env.Service.SwitchAsync(env.Profiles, b.Id, AutoOpts);

        Assert.Equal(SwitchOutcome.Success, result.Outcome);
        Assert.Equal(bBytes, env.ReadActiveSlot());
        Assert.True(b.IsActive);
        Assert.False(a.IsActive);
        Assert.Equal(env.Clock.UtcNow, b.LastSwitchedAt);
    }

    [Fact]
    public async Task WriteBack_PreservesRenewedTokens_OfOutgoingAccount()
    {
        // O cofre de A guarda a versão v1, mas o Codex renovou o slot ativo para v2 durante o uso.
        using var env = new Env();
        var aV1 = Sample.AuthJson(accountId: "acct_A", refreshToken: "rt-A-OLD", lastRefresh: "2026-06-20T00:00:00Z");
        var aV2 = Sample.AuthJson(accountId: "acct_A", refreshToken: "rt-A-NEW", lastRefresh: "2026-06-30T00:00:00Z");
        var bBytes = Sample.AuthJson(accountId: "acct_B");
        var a = env.AddProfile("A", aV1, active: true);
        var b = env.AddProfile("B", bBytes, active: false);
        env.SetActiveSlot(aV2); // Codex reescreveu o slot com o token novo.

        await env.Service.SwitchAsync(env.Profiles, b.Id, AutoOpts);

        // O cofre de A deve agora conter v2 (token renovado), não a v1 antiga — ponto 2/3.
        Assert.Equal(aV2, env.Vault.LoadBlob(a.Id));
        Assert.Equal(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero), a.LastRefreshedAt);
    }

    [Fact]
    public async Task Backup_OfPreviousActive_IsCreated_AndDecryptsToOriginal()
    {
        using var env = new Env();
        var aBytes = Sample.AuthJson(accountId: "acct_A");
        var bBytes = Sample.AuthJson(accountId: "acct_B");
        _ = env.AddProfile("A", aBytes, active: true);
        var b = env.AddProfile("B", bBytes, active: false);
        env.SetActiveSlot(aBytes);

        await env.Service.SwitchAsync(env.Profiles, b.Id, AutoOpts);

        var backups = env.Fs.EnumerateFiles(env.BackupsDir, "auth-*.bin");
        Assert.Single(backups);
        Assert.Equal(aBytes, env.Vault.LoadEncryptedFile(backups[0]));
    }

    [Fact]
    public async Task Rollback_OnSlotWriteFailure_RestoresOriginalActive_AndKeepsOriginActive()
    {
        // ESTE é o teste-chave: se a gravação do novo slot falhar, a conta original NÃO se perde.
        using var env = new Env();
        var aBytes = Sample.AuthJson(accountId: "acct_A", refreshToken: "rt-A");
        var bBytes = Sample.AuthJson(accountId: "acct_B", refreshToken: "rt-B");
        var a = env.AddProfile("A", aBytes, active: true);
        var b = env.AddProfile("B", bBytes, active: false);
        env.SetActiveSlot(aBytes);
        env.Proc.Running = [DesktopApp()];

        env.Fs.FailAtomicWriteForPath = env.Paths.ActiveAuthPath; // falha só ao gravar o slot ativo

        var result = await env.Service.SwitchAsync(env.Profiles, b.Id, AutoOpts);

        Assert.Equal(SwitchOutcome.RolledBack, result.Outcome);
        Assert.Equal(aBytes, env.ReadActiveSlot());   // slot ativo intacto = conta A
        Assert.True(a.IsActive);                        // A continua ativa
        Assert.False(b.IsActive);                       // B não assumiu
        Assert.Contains(env.Proc.Relaunched, p => p.Kind == CodexProcessKind.DesktopApp); // apps reabertos
    }

    [Fact]
    public async Task AntiRace_AbortsWhenCodexStillRunning_SlotUntouched()
    {
        using var env = new Env();
        var aBytes = Sample.AuthJson(accountId: "acct_A");
        var bBytes = Sample.AuthJson(accountId: "acct_B");
        var a = env.AddProfile("A", aBytes, active: true);
        var b = env.AddProfile("B", bBytes, active: false);
        env.SetActiveSlot(aBytes);
        env.Proc.Running = [DesktopApp(), Cli()];
        env.Proc.RemnantAfterClose = true; // uma CLI sobrevive ao encerramento

        var result = await env.Service.SwitchAsync(env.Profiles, b.Id, AutoOpts);

        Assert.Equal(SwitchOutcome.AbortedProcessRemnant, result.Outcome);
        Assert.Equal(aBytes, env.ReadActiveSlot()); // nada tocado
        Assert.True(a.IsActive);
        Assert.False(b.IsActive);
    }

    [Fact]
    public async Task TargetUndecryptable_FailsFast_WithoutClosingApps()
    {
        using var env = new Env();
        var aBytes = Sample.AuthJson(accountId: "acct_A");
        var a = env.AddProfile("A", aBytes, active: true);
        var b = env.AddProfile("B", Sample.AuthJson(accountId: "acct_B"), active: false);
        env.SetActiveSlot(aBytes);
        env.Proc.Running = [DesktopApp()];

        // Corrompe o blob de B: bytes que o DPAPI não consegue decifrar.
        File.WriteAllBytes(env.Vault.BlobPath(b.Id), [1, 2, 3, 4, 5, 6, 7, 8]);

        var result = await env.Service.SwitchAsync(env.Profiles, b.Id, AutoOpts);

        Assert.Equal(SwitchOutcome.Failed, result.Outcome);
        Assert.Equal(ErrorCategory.DecryptionFailed, result.Error!.Category);
        Assert.False(env.Proc.CloseCalled);           // nenhum app foi fechado
        Assert.Equal(aBytes, env.ReadActiveSlot());    // slot intacto
        Assert.True(a.IsActive);
    }

    [Fact]
    public async Task NoManagedActiveProfile_SkipsWriteBack_StillSwitches()
    {
        using var env = new Env();
        var bBytes = Sample.AuthJson(accountId: "acct_B");
        var b = env.AddProfile("B", bBytes, active: false); // nenhum perfil ativo
        // slot ativo pertence a conta não gerenciada
        env.SetActiveSlot(Sample.AuthJson(accountId: "acct_unmanaged"));

        var result = await env.Service.SwitchAsync(env.Profiles, b.Id, AutoOpts);

        Assert.Equal(SwitchOutcome.Success, result.Outcome);
        Assert.Equal(bBytes, env.ReadActiveSlot());
        Assert.True(b.IsActive);
    }

    [Fact]
    public async Task DoNothingMode_DoesNotTouchProcesses()
    {
        using var env = new Env();
        var aBytes = Sample.AuthJson(accountId: "acct_A");
        var bBytes = Sample.AuthJson(accountId: "acct_B");
        _ = env.AddProfile("A", aBytes, active: true);
        var b = env.AddProfile("B", bBytes, active: false);
        env.SetActiveSlot(aBytes);
        env.Proc.Running = [DesktopApp()];

        var opts = AutoOpts with { CloseReopenMode = CloseReopenMode.DoNothing };
        var result = await env.Service.SwitchAsync(env.Profiles, b.Id, opts);

        Assert.Equal(SwitchOutcome.Success, result.Outcome);
        Assert.False(env.Proc.CloseCalled);
        Assert.Empty(env.Proc.Relaunched);
        Assert.Equal(bBytes, env.ReadActiveSlot());
    }

    [Fact]
    public async Task SwitchingToAlreadyActive_IsNoOp()
    {
        using var env = new Env();
        var aBytes = Sample.AuthJson(accountId: "acct_A");
        var a = env.AddProfile("A", aBytes, active: true);
        env.SetActiveSlot(aBytes);

        var result = await env.Service.SwitchAsync(env.Profiles, a.Id, AutoOpts);

        Assert.Equal(SwitchOutcome.Success, result.Outcome);
        Assert.False(env.Proc.CloseCalled);
    }
}
