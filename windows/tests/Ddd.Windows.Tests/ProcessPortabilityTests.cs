using Ddd.CompatSigner;
using Xunit;

namespace Ddd.Windows.Tests;

public sealed class ProcessPortabilityTests
{
    private static string Signer => Path.Combine(AppContext.BaseDirectory, "CompatSigner",
        OperatingSystem.IsWindows() ? "Ddd.CompatSigner.exe" : "Ddd.CompatSigner");

    [Fact]
    public async Task EarlyNonzeroExitPreservesDiagnosticsDespiteBrokenStdinPipe()
    {
        // Much larger than a pipe buffer, guaranteeing the child cannot receive all
        // input before it exits. A one-byte input only reproduces this race sometimes.
        var result = await Tool.RunAsync(Signer, ["--probe-exit-without-reading-stdin", "7"],
            AppContext.BaseDirectory, new byte[8 * 1024 * 1024]);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("probe stdout", System.Text.Encoding.UTF8.GetString(result.Output));
        Assert.Equal("probe stderr", result.Error);
    }

    [Fact]
    public async Task EarlyZeroExitCannotHideFailedStdinDelivery()
    {
        var error = await Assert.ThrowsAsync<IOException>(() => Tool.RunAsync(Signer,
            ["--probe-exit-without-reading-stdin", "0"], AppContext.BaseDirectory, new byte[8 * 1024 * 1024]));
        Assert.Contains("before all supplied stdin", error.Message);
    }

    [Fact]
    public void ScratchCleanupRemovesReadOnlyGitObjects()
    {
        string root = Path.Combine(Path.GetTempPath(), "ddd-cleanup-" + Guid.NewGuid().ToString("N"));
        string objects = Path.Combine(root, ".git", "objects", "4b");
        Directory.CreateDirectory(objects);
        string path = Path.Combine(objects, "825dc642cb6eb9a060e54bf8d69288fbee4904");
        File.WriteAllText(path, "test-only object stand-in");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try
        {
            CompatibilityTests.DeleteScratchDirectory(root);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
