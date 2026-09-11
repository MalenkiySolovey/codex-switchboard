using CodexSwitcher.Core.Tests.TestSupport;

namespace CodexSwitcher.Core.Tests;

public sealed class AuditLogTests
{
    [Fact]
    public void Record_AppendsLines_WithActionAndOutcome()
    {
        using var dir = new TempDir();
        var log = new FileAuditLog(dir.Combine("audit.log"));

        log.Record("switch", "ok", "A -> B");
        log.Record("refresh", "needs-relogin", "B");

        var lines = File.ReadAllLines(dir.Combine("audit.log"));
        Assert.Equal(2, lines.Length);
        Assert.Contains("switch", lines[0]);
        Assert.Contains("ok", lines[0]);
        Assert.Contains("refresh", lines[1]);
    }

    [Fact]
    public void Record_SanitizesTabsAndNewlines()
    {
        using var dir = new TempDir();
        var log = new FileAuditLog(dir.Combine("audit.log"));

        log.Record("switch", "ok", "line1\nline2\tcol");

        var lines = File.ReadAllLines(dir.Combine("audit.log"));
        Assert.Single(lines); // detalhe não quebra em várias linhas
    }
}
