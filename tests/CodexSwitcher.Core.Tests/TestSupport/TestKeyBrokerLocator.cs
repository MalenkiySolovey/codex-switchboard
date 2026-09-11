namespace CodexSwitcher.Core.Tests.TestSupport;

public static class TestKeyBrokerLocator
{
    public static string FindKeyBrokerBinary()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "CodexSwitchboard.KeyBroker.exe"),
            Path.Combine(baseDir, "broker", "CodexSwitchboard.KeyBroker.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "src", "CodexSwitchboard.KeyBroker", "bin", "Debug", "net10.0-windows", "CodexSwitchboard.KeyBroker.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "src", "CodexSwitchboard.KeyBroker", "bin", "Release", "net10.0-windows", "CodexSwitchboard.KeyBroker.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "CodexSwitchboard.KeyBroker", "bin", "Debug", "net10.0-windows", "CodexSwitchboard.KeyBroker.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "CodexSwitchboard.KeyBroker", "bin", "Release", "net10.0-windows", "CodexSwitchboard.KeyBroker.exe"),
        };

        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }

        throw new FileNotFoundException("KeyBroker executable not found in test search paths.");
    }
}
