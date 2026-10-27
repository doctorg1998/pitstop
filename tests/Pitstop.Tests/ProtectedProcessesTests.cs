using Pitstop.Core.Profiles;

namespace Pitstop.Tests;

public class ProtectedProcessesTests
{
    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("EXPLORER")]
    [InlineData(" lsass.exe ")]
    [InlineData("Pitstop.exe")]
    public void System_processes_are_protected(string name) =>
        Assert.True(ProtectedProcesses.IsProtected(name));

    [Theory]
    [InlineData("OneDrive.exe")]
    [InlineData("Discord.exe")]
    [InlineData("explorer2.exe")]
    public void Ordinary_apps_are_not_protected(string name) =>
        Assert.False(ProtectedProcesses.IsProtected(name));
}
