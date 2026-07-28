using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Support;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Io;
using CodexSwitcher.Infra.Security;

namespace CodexSwitcher.Core.Tests;

public sealed class ProfileServiceTests
{
    private sealed class Env : IDisposable
    {
        public TempDir Dir { get; } = new();
        public ProfileService Service { get; }
        public FakeClock Clock { get; } = new();

        public Env()
        {
            var fs = new PhysicalFileSystem();
            var vault = new VaultService(new DpapiSecretProtector(), fs, Dir.Combine("vault"));
            var store = new ProfileStore(fs, Dir.Combine("profiles.json"));
            var paths = CodexSwitcher.Core.Models.CodexPaths.ForHome(Dir.Combine(".codex"));
            var recon = new ReconciliationService(fs, paths);
            Service = new ProfileService(vault, store, recon, fs, paths, Clock, new FakeAudit());
        }

        public void Dispose() => Dir.Dispose();
    }

    [Fact]
    public void AddFromAuthJson_CreatesProfile_WithIdentity()
    {
        using var env = new Env();
        var token = Sample.Jwt(sub: "sub-1", email: "user@x.com");
        var bytes = Sample.AuthJson(idToken: token);

        var p = env.Service.AddFromAuthJson(bytes, nickname: "Trabalho");

        Assert.Equal("Trabalho", p.Nickname);
        Assert.Equal("sub-1", p.AccountSub);
        Assert.Equal("user@x.com", p.AccountEmail);
        Assert.Single(env.Service.Profiles);
    }

    [Fact]
    public void AddFromAuthJson_SameSub_DeduplicatesAndUpdates()
    {
        using var env = new Env();
        var token = Sample.Jwt(sub: "sub-dup", email: "a@b.com");
        var first = Sample.AuthJson(idToken: token, refreshToken: "rt-1");
        var second = Sample.AuthJson(idToken: token, refreshToken: "rt-2");

        var p1 = env.Service.AddFromAuthJson(first, "Nome1");
        var p2 = env.Service.AddFromAuthJson(second, null);

        Assert.Same(p1, p2);                       // mesmo perfil (dedupe por sub)
        Assert.Single(env.Service.Profiles);
    }

    [Fact]
    public void Remove_DeletesBlobAndMetadata()
    {
        using var env = new Env();
        var p = env.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "s")), "X");

        env.Service.Remove(p.Id);

        Assert.Empty(env.Service.Profiles);
    }

    [Fact]
    public void Reload_PersistsAcrossInstances()
    {
        using var env = new Env();
        env.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "s1")), "One");

        env.Service.Load(); // recarrega do disco

        Assert.Single(env.Service.Profiles);
        Assert.Equal("One", env.Service.Profiles[0].Nickname);
    }

    [Fact]
    public void MarkUsed_SetsTimestampToNow()
    {
        using var env = new Env();
        var p = env.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "s")), "X");

        env.Service.MarkUsed(p.Id);

        Assert.Equal(env.Clock.UtcNow, p.MarkedUsedAt);
    }

    [Fact]
    public void UnmarkUsed_ClearsTimestamp()
    {
        using var env = new Env();
        var p = env.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "s")), "X");
        env.Service.MarkUsed(p.Id);

        env.Service.UnmarkUsed(p.Id);

        Assert.Null(p.MarkedUsedAt);
    }

    [Fact]
    public void Reorder_AssignsSequentialSortOrder()
    {
        using var env = new Env();
        var a = env.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "a")), "A");
        var b = env.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "b")), "B");
        var c = env.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "c")), "C");

        env.Service.Reorder([c.Id, a.Id, b.Id]);

        Assert.Equal(0, c.SortOrder);
        Assert.Equal(1, a.SortOrder);
        Assert.Equal(2, b.SortOrder);
    }

    [Fact]
    public void Reorder_PersistsAcrossReload()
    {
        using var env = new Env();
        var a = env.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "a")), "A");
        var b = env.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "b")), "B");

        env.Service.Reorder([b.Id, a.Id]);
        env.Service.Load();

        Assert.Equal("B", env.Service.Profiles.OrderBy(p => p.SortOrder).First().Nickname);
    }

    [Fact]
    public void ExportOne_ThenImport_RestoresAccountAndNickname()
    {
        using var source = new Env();
        var original = source.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "portable", email: "p@x.com")), "Portátil");
        var exported = source.Service.ExportOne(original.Id);

        using var destination = new Env();
        var count = destination.Service.Import(exported);

        var imported = Assert.Single(destination.Service.Profiles);
        Assert.Equal(1, count);
        Assert.Equal("portable", imported.AccountSub);
        Assert.Equal("Portátil", imported.Nickname);
    }

    [Fact]
    public void ExportAll_ThenImport_RestoresAllAccountsInOrder()
    {
        using var source = new Env();
        var first = source.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "first")), "First");
        var second = source.Service.AddFromAuthJson(Sample.AuthJson(idToken: Sample.Jwt(sub: "second")), "Second");
        source.Service.Reorder([second.Id, first.Id]);

        using var destination = new Env();
        var count = destination.Service.Import(source.Service.ExportAll());

        Assert.Equal(2, count);
        Assert.Equal(["Second", "First"], destination.Service.Profiles.OrderBy(p => p.SortOrder).Select(p => p.Nickname));
    }

    [Fact]
    public void Import_InvalidDocument_ThrowsWithoutChangingProfiles()
    {
        using var env = new Env();

        Assert.Throws<InvalidDataException>(() => env.Service.Import("{}"));
        Assert.Empty(env.Service.Profiles);
    }

    [Fact]
    public void Import_RawAuthJson_AddsOneAccount()
    {
        using var env = new Env();
        var authJson = System.Text.Encoding.UTF8.GetString(
            Sample.AuthJson(idToken: Sample.Jwt(sub: "raw", email: "raw@x.com")));

        var count = env.Service.Import(authJson);

        Assert.Equal(1, count);
        Assert.Equal("raw", Assert.Single(env.Service.Profiles).AccountSub);
    }
}

public sealed class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Null_IsNever() => Assert.Equal("nunca", RelativeTime.Humanize(null, Now));

    [Fact]
    public void JustNow() => Assert.Equal("agora mesmo", RelativeTime.Humanize(Now.AddSeconds(-30), Now));

    [Fact]
    public void Minutes() => Assert.Equal("há 5 min", RelativeTime.Humanize(Now.AddMinutes(-5), Now));

    [Fact]
    public void Yesterday() => Assert.Equal("ontem", RelativeTime.Humanize(Now.AddDays(-1), Now));

    [Fact]
    public void Days() => Assert.Equal("há 3 dias", RelativeTime.Humanize(Now.AddDays(-3), Now));
}
