using CodexSwitcher.Core.Security;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Io;
using CodexSwitcher.Infra.Security;

namespace CodexSwitcher.Core.Tests;

/// <summary>
/// Prova, com DPAPI e disco reais, que o cofre armazena e recupera o auth.json de várias contas
/// sem corromper nem perder nada — o coração do requisito "não perder o login das múltiplas contas".
/// </summary>
public sealed class VaultServiceTests
{
    private static VaultService NewVault(TempDir dir) =>
        new(new DpapiSecretProtector(), new PhysicalFileSystem(), dir.Combine("vault"));

    [Fact]
    public void SaveThenLoad_ReturnsIdenticalBytes()
    {
        using var dir = new TempDir();
        var vault = NewVault(dir);
        var id = Guid.NewGuid();
        var original = Sample.AuthJson();

        vault.SaveBlob(id, original);
        var loaded = vault.LoadBlob(id);

        Assert.Equal(original, loaded);
    }

    [Fact]
    public void OnDisk_IsEncrypted_NotPlaintext()
    {
        using var dir = new TempDir();
        var vault = NewVault(dir);
        var id = Guid.NewGuid();
        var original = Sample.AuthJson(refreshToken: "rt-super-secret-marker");

        vault.SaveBlob(id, original);

        var rawOnDisk = File.ReadAllBytes(vault.BlobPath(id));
        var asText = System.Text.Encoding.UTF8.GetString(rawOnDisk);
        Assert.DoesNotContain("rt-super-secret-marker", asText);
        Assert.DoesNotContain("refresh_token", asText);
        Assert.NotEqual(original, rawOnDisk);
    }

    [Fact]
    public void PreservesUnknownFields_ByteForByte()
    {
        using var dir = new TempDir();
        var vault = NewVault(dir);
        var id = Guid.NewGuid();
        // Contém "future_unknown_field" que o app nunca parseia (blob opaco, ponto 11).
        var original = Sample.AuthJson();

        vault.SaveBlob(id, original);
        var loaded = vault.LoadBlob(id);

        Assert.Equal(original, loaded);
        Assert.Contains("future_unknown_field", System.Text.Encoding.UTF8.GetString(loaded));
    }

    [Fact]
    public void MultipleAccounts_StoredIndependently_AllRecoverable()
    {
        using var dir = new TempDir();
        var vault = NewVault(dir);

        var accounts = Enumerable.Range(0, 5)
            .Select(i => (Id: Guid.NewGuid(), Bytes: Sample.AuthJson(accountId: $"acct_{i}", refreshToken: $"rt-{i}")))
            .ToList();

        foreach (var (accId, bytes) in accounts)
            vault.SaveBlob(accId, bytes);

        foreach (var (accId, bytes) in accounts)
            Assert.Equal(bytes, vault.LoadBlob(accId));
    }

    [Fact]
    public void Fingerprint_IsStable_AndMatchesContent()
    {
        using var dir = new TempDir();
        var vault = NewVault(dir);
        var id = Guid.NewGuid();
        var bytes = Sample.AuthJson();

        var fp1 = vault.SaveBlob(id, bytes);
        var fp2 = vault.SaveBlob(id, bytes);

        Assert.Equal(fp1, fp2);
        Assert.Equal(Fingerprint.Compute(bytes), fp1);
        Assert.Equal(64, fp1.Length); // SHA-256 hex
    }

    [Fact]
    public void Overwrite_UpdatesContent_AndFingerprint()
    {
        using var dir = new TempDir();
        var vault = NewVault(dir);
        var id = Guid.NewGuid();

        var v1 = Sample.AuthJson(lastRefresh: "2026-06-20T00:00:00Z");
        var v2 = Sample.AuthJson(lastRefresh: "2026-06-28T00:00:00Z");

        var fp1 = vault.SaveBlob(id, v1);
        var fp2 = vault.SaveBlob(id, v2);

        Assert.NotEqual(fp1, fp2);
        Assert.Equal(v2, vault.LoadBlob(id));
    }

    [Fact]
    public void DeleteBlob_RemovesFile()
    {
        using var dir = new TempDir();
        var vault = NewVault(dir);
        var id = Guid.NewGuid();
        vault.SaveBlob(id, Sample.AuthJson());
        Assert.True(vault.Exists(id));

        vault.DeleteBlob(id);

        Assert.False(vault.Exists(id));
    }

    [Fact]
    public void SaveBlobIfUnchanged_WhenOriginalFingerprintMatches_SucceedsAndUpdates()
    {
        using var dir = new TempDir();
        var vault = NewVault(dir);
        var id = Guid.NewGuid();

        var initial = Sample.AuthJson(lastRefresh: "2026-06-20T00:00:00Z");
        var initialFp = vault.SaveBlob(id, initial);

        var updated = Sample.AuthJson(lastRefresh: "2026-06-21T00:00:00Z");
        var (success, newFp) = vault.SaveBlobIfUnchanged(id, initialFp, updated);

        Assert.True(success);
        Assert.NotNull(newFp);
        Assert.NotEqual(initialFp, newFp);
        Assert.Equal(updated, vault.LoadBlob(id));
    }

    [Fact]
    public void SaveBlobIfUnchanged_WhenFingerprintDiffers_ReturnsFalseWithCurrentFingerprint()
    {
        using var dir = new TempDir();
        var vault = NewVault(dir);
        var id = Guid.NewGuid();

        var initial = Sample.AuthJson(lastRefresh: "2026-06-20T00:00:00Z");
        var initialFp = vault.SaveBlob(id, initial);

        // Simulate concurrent mutation in vault (e.g. user performed switch or external write)
        var concurrent = Sample.AuthJson(lastRefresh: "2026-06-21T12:00:00Z");
        var concurrentFp = vault.SaveBlob(id, concurrent);

        // Attempt CAS with stale expected original fingerprint
        var staleUpdated = Sample.AuthJson(lastRefresh: "2026-06-22T00:00:00Z");
        var (success, currentFp) = vault.SaveBlobIfUnchanged(id, initialFp, staleUpdated);

        Assert.False(success);
        Assert.Equal(concurrentFp, currentFp);
        Assert.Equal(concurrent, vault.LoadBlob(id));
    }

    [Fact]
    public void SaveBlobIfUnchanged_WhenProfileDoesNotExist_ReturnsFalse()
    {
        using var dir = new TempDir();
        var vault = NewVault(dir);
        var id = Guid.NewGuid();

        var (success, currentFp) = vault.SaveBlobIfUnchanged(id, "dummy-fingerprint", Sample.AuthJson());

        Assert.False(success);
        Assert.Null(currentFp);
    }

    [Fact]
    public async Task SaveBlobIfUnchanged_ConcurrentRace_ExactlyOneCommits()
    {
        using var dir = new TempDir();
        var vault = NewVault(dir);
        var id = Guid.NewGuid();

        var initial = Sample.AuthJson(lastRefresh: "2026-06-20T00:00:00Z");
        var initialFp = vault.SaveBlob(id, initial);

        // 5 concurrent attempts starting at the same time with the same expected fingerprint
        var candidates = Enumerable.Range(1, 5)
            .Select(i => Sample.AuthJson(lastRefresh: $"2026-06-2{i}T00:00:00Z"))
            .ToList();

        using var barrier = new Barrier(candidates.Count);

        var tasks = candidates.Select(candidate => Task.Run(() =>
        {
            barrier.SignalAndWait();
            return vault.SaveBlobIfUnchanged(id, initialFp, candidate);
        })).ToList();

        var results = await Task.WhenAll(tasks);

        var successCount = results.Count(r => r.Success);
        var failureCount = results.Count(r => !r.Success);

        Assert.Equal(1, successCount);
        Assert.Equal(4, failureCount);

        // All failed attempts report the current committed fingerprint
        var winnerFp = results.First(r => r.Success).CurrentFingerprint;
        Assert.NotNull(winnerFp);

        foreach (var failure in results.Where(r => !r.Success))
        {
            Assert.Equal(winnerFp, failure.CurrentFingerprint);
        }
    }
}
