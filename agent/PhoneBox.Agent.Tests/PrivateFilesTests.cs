using PhoneBox.Agent;
using Xunit;

public sealed class PrivateFilesTests
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    [Fact]
    public void SavedConfigAndItsBackupAreOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows()) return; // Windows keeps the profile folder's ACLs
        var dir = Directory.CreateTempSubdirectory("phonebox-private-").FullName;
        try
        {
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, "{}");
            File.SetUnixFileMode(path, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead); // 0644, as older versions left it
            var store = new ConfigStore(path);
            Assert.Null(store.Save("{}"));
            Assert.Equal(OwnerOnly, File.GetUnixFileMode(path));
            Assert.Equal(OwnerOnly, File.GetUnixFileMode(path + ".bak"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void RestrictToOwnerTightensAnExistingFileAndIgnoresAMissingOne()
    {
        var dir = Directory.CreateTempSubdirectory("phonebox-private-").FullName;
        try
        {
            var path = Path.Combine(dir, "data.json");
            AppPaths.RestrictToOwner(path); // missing: no exception
            AppPaths.WriteAllTextPrivate(path, "{\"a\":1}");
            Assert.Equal("{\"a\":1}", File.ReadAllText(path));
            if (OperatingSystem.IsWindows()) return;
            Assert.Equal(OwnerOnly, File.GetUnixFileMode(path));
            File.SetUnixFileMode(path, OwnerOnly | UnixFileMode.OtherRead);
            AppPaths.RestrictToOwner(path);
            Assert.Equal(OwnerOnly, File.GetUnixFileMode(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void CreatePrivateReplacesALeftoverFileInsteadOfWritingIntoIt()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("phonebox-private-").FullName;
        try
        {
            var path = Path.Combine(dir, "config.json.tmp");
            File.WriteAllText(path, "old leftover content that is longer");
            File.SetUnixFileMode(path, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            AppPaths.WriteAllTextPrivate(path, "new");
            Assert.Equal("new", File.ReadAllText(path));
            Assert.Equal(OwnerOnly, File.GetUnixFileMode(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void RestrictConfigTightensABackupEvenWithoutTheConfig()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("phonebox-private-").FullName;
        try
        {
            var path = Path.Combine(dir, "custom.json");
            File.WriteAllText(path + ".bak", "{}");
            File.SetUnixFileMode(path + ".bak", OwnerOnly | UnixFileMode.OtherRead);
            AppPaths.RestrictConfig(path);
            Assert.Equal(OwnerOnly, File.GetUnixFileMode(path + ".bak"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
