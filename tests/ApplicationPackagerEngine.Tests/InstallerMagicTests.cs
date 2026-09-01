using ApplicationPackagerEngine.Services;
using Xunit;

namespace ApplicationPackagerEngine.Tests;

public sealed class InstallerMagicTests
{
    private static string WriteBytes(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), "magic-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(p, bytes);
        return p;
    }

    [Fact]
    public void Exe_WithMzHeader_Passes()
    {
        var p = WriteBytes([(byte)'M', (byte)'Z', 0, 0, 0, 0, 0, 0, 1, 2]);
        StagingService.ValidateInstallerMagic(p, "EXE");
        File.Delete(p);
    }

    [Fact]
    public void Exe_WithHtmlBody_Throws()
    {
        var p = WriteBytes(System.Text.Encoding.ASCII.GetBytes("<!DOCTYPE html><html>"));
        var ex = Assert.Throws<InvalidOperationException>(() => StagingService.ValidateInstallerMagic(p, "EXE"));
        Assert.Contains("HTML", ex.Message);
        File.Delete(p);
    }

    [Fact]
    public void Msi_WithOleMagic_Passes_AndMzThrows()
    {
        var ole = WriteBytes([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);
        StagingService.ValidateInstallerMagic(ole, "MSI");
        File.Delete(ole);
        var mz = WriteBytes([(byte)'M', (byte)'Z', 0, 0, 0, 0, 0, 0]);
        Assert.Throws<InvalidOperationException>(() => StagingService.ValidateInstallerMagic(mz, "MSI"));
        File.Delete(mz);
    }

    [Fact]
    public void OtherTypes_AndTinyFiles_AreHandled()
    {
        var p = WriteBytes([1, 2, 3]);
        StagingService.ValidateInstallerMagic(p, "ODT"); // not checked
        Assert.Throws<InvalidOperationException>(() => StagingService.ValidateInstallerMagic(p, "EXE"));
        File.Delete(p);
    }
}
