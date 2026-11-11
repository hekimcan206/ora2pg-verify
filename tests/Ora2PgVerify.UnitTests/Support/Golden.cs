namespace Ora2PgVerify.UnitTests.Support;

/// <summary>
/// Approval-style golden files under tests/Ora2PgVerify.UnitTests/Golden. Run with UPDATE_GOLDEN=1 to accept a
/// deliberate change, then review the diff in version control like any other code change.
/// </summary>
internal static class Golden
{
    public static string Directory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ora2PgVerify.slnx")))
            {
                dir = dir.Parent;
            }

            return dir is null
                ? Path.Combine(AppContext.BaseDirectory, "Golden")
                : Path.Combine(dir.FullName, "tests", "Ora2PgVerify.UnitTests", "Golden");
        }
    }

    public static void Verify(string name, string actual)
    {
        actual = actual.Replace("\r\n", "\n", StringComparison.Ordinal);
        var path = Path.Combine(Directory, name);
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        File.Exists(path).Should().BeTrue($"golden file {name} must exist (run once with UPDATE_GOLDEN=1 and review it)");
        var expected = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        actual.Should().Be(expected, $"output must match golden file {name}; if the change is intended, re-run with UPDATE_GOLDEN=1 and review the diff");
    }
}
