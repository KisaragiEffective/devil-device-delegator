using System.Text;
using System.Text.Json;
using Ddd.CompatSigner;
using Ddd.Windows.Git;
using Xunit;

namespace Ddd.Windows.Tests;

public sealed class CompatibilityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddd compat 署名 " + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> environment = new();
    private string Key => Path.Combine(root, "test signing key");
    private string Capture => Path.Combine(root, "capture");
    private string Allowed => Path.Combine(root, "allowed signers");
    private string Signer => Path.Combine(root, "signer ü 署名", OperatingSystem.IsWindows() ? "Ddd.CompatSigner.exe" : "Ddd.CompatSigner");

    public CompatibilityTests()
    {
        Directory.CreateDirectory(root);
        string signerDirectory = Path.GetDirectoryName(Signer)!;
        Directory.CreateDirectory(signerDirectory);
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "CompatSigner")))
            File.Copy(file, Path.Combine(signerDirectory, Path.GetFileName(file)));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Signer, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string config = Path.Combine(root, "empty-gitconfig");
        File.WriteAllText(config, "");
        environment["GIT_CONFIG_GLOBAL"] = config;
        environment["GIT_CONFIG_SYSTEM"] = config;
        environment["GIT_CONFIG_NOSYSTEM"] = "1";
        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GIT_EDITOR"] = "true";
        environment["GIT_AUTHOR_DATE"] = "1700000000 +0000";
        environment["GIT_COMMITTER_DATE"] = "1700000000 +0000";
        environment["SSH_AUTH_SOCK"] = null;
        foreach (string name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_CONFIG_COUNT", "GIT_CONFIG_PARAMETERS", "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL", "DDD_COMPAT_MODE" })
            environment[name] = null;
        environment["DDD_COMPAT_PRIVATE_KEY"] = Key;
        environment["DDD_COMPAT_CAPTURE"] = Capture;
    }

    private Task<ToolResult> Run(string program, IEnumerable<string> arguments, byte[]? input = null) =>
        Tool.RunAsync(program, arguments, root, input, environment);

    private Task<ToolResult> Git(params string[] args) => Run("git", new[] { "--no-pager" }.Concat(args));

    private static void Success(ToolResult result) => Assert.True(result.ExitCode == 0,
        $"Tool exit {result.ExitCode}: {result.Error}\n{Encoding.UTF8.GetString(result.Output)}");

    private async Task GenerateKey(string path)
    {
        Success(await Run("ssh-keygen", ["-q", "-t", "ecdsa", "-b", "256", "-N", "", "-C", "ddd-test-only", "-f", path]));
    }

    private async Task SetupKey()
    {
        await GenerateKey(Key);
        await File.WriteAllTextAsync(Allowed, "ddd-test namespaces=\"git\" " + await File.ReadAllTextAsync(Key + ".pub"), new UTF8Encoding(false));
    }

    [Fact]
    public async Task PublicOnlyGoldenVerifiesAndItsPayloadHashIsStable()
    {
        string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        byte[] payload = await File.ReadAllBytesAsync(Path.Combine(fixtures, "commit-payload-v1.txt"));
        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixtures, "provenance-v1.json")));
        Assert.Equal(metadata.RootElement.GetProperty("payloadSha256").GetString(),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant());
        Assert.False(metadata.RootElement.GetProperty("privateKeyIncluded").GetBoolean());
        await File.WriteAllTextAsync(Allowed, "ddd-fixture namespaces=\"git\" " + await File.ReadAllTextAsync(Path.Combine(fixtures, "p256-public-v1.pub")));
        string[] verify = ["-Y", "verify", "-f", Allowed, "-I", "ddd-fixture", "-n", "git", "-s", Path.Combine(fixtures, "p256-signature-v1.sshsig")];
        Success(await Run("ssh-keygen", verify, payload));
        payload[^1] ^= 1;
        Assert.NotEqual(0, (await Run("ssh-keygen", verify, payload)).ExitCode);
    }

    [Fact]
    public async Task ProductionExecutableCannotUseTestSoftwareSignerEnvironment()
    {
        string helper = Path.Combine(AppContext.BaseDirectory, "CompatSigner");
        var result = await Run("dotnet", ["exec", "--runtimeconfig", Path.Combine(helper, "Ddd.CompatSigner.runtimeconfig.json"),
            Path.Combine(helper, "Ddd.Windows.dll"), "-Y", "sign", "-n", "git", "-f", Key + ".pub", "-"], [1]);
        Assert.Equal(SigningHost.SigningFailed, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("DDD: signing failed", result.Error);
        Assert.False(Directory.Exists(Capture));
    }

    [Fact]
    public async Task GitCommitCapturesContractAndVerifiesWithOrdinaryGit()
    {
        await SetupKey();
        Success(await Git("init", "--initial-branch=main"));
        Success(await Git("config", "user.name", "DDD Test"));
        Success(await Git("config", "user.email", "ddd@example.invalid"));
        Success(await Git("config", "gpg.format", "ssh"));
        Success(await Git("config", "user.signingkey", Key + ".pub"));
        // This setting is an executable path, not a shell command or a quoted command line.
        Success(await Git("config", "gpg.ssh.program", Signer.Replace('\\', '/')));
        Success(await Git("config", "gpg.ssh.allowedSignersFile", Allowed));
        var commit = await Git("commit", "--allow-empty", "-S", "-m", "Compatibility fixture — 署名対象\n\nThese UTF-8 bytes, including the final LF, are the signing payload.");
        Assert.True(commit.ExitCode == 0, commit.Error + "\nObserved argv: " + await File.ReadAllTextAsync(Path.Combine(Capture, "argv.json")));
        string[] actual = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Path.Combine(Capture, "argv.json")))!;
        string[] expected = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "git-sign-argv-v1.json")))!;
        Assert.Equal(7, actual.Length);
                Assert.NotEqual("-", actual[6]);
                expected = expected.Select(value => value switch
                {
                    "{publicKeyPath}" => Key + ".pub",
                    "{payloadPath}" => actual[6],
                    _ => value
                }).ToArray();
        Assert.Equal(expected, actual);
        byte[] payload = await File.ReadAllBytesAsync(Path.Combine(Capture, "payload.bin"));
        byte[] fixture = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "commit-payload-v1.txt"));
        Assert.Equal(fixture, payload);
        Success(await Git("-c", "gpg.ssh.program=ssh-keygen", "verify-commit", "HEAD"));
        // Verification is intentionally not part of the v1 signing adapter's compatible surface.
        Assert.NotEqual(0, (await Git("verify-commit", "HEAD")).ExitCode);
        Assert.False(File.Exists(Path.Combine(root, "-.sig")));
        Assert.Empty(Directory.GetFiles(root, "*.sig"));
                Assert.False(File.Exists(actual[6]));
                Assert.False(File.Exists(actual[6] + ".sig"));
    }

    [Fact]
    public async Task OpenSshVerifiesExactBytesAndRejectsTamperingNamespaceAndWrongKey()
    {
        await SetupKey();
        byte[] payload = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "commit-payload-v1.txt"));
        var signed = await Run(Signer, ["-Y", "sign", "-n", "git", "-f", Key + ".pub", "-"], payload);
        Success(signed);
        Assert.Empty(signed.Error);
        string signature = Path.Combine(root, "response.sshsig");
        await File.WriteAllBytesAsync(signature, signed.Output);
        string[] verify = ["-Y", "verify", "-f", Allowed, "-I", "ddd-test", "-n", "git", "-s", signature];
        Success(await Run("ssh-keygen", verify, payload));
        payload[0] ^= 1;
        Assert.NotEqual(0, (await Run("ssh-keygen", verify, payload)).ExitCode);
        payload[0] ^= 1;
        string[] otherNamespace = ["-Y", "verify", "-f", Allowed, "-I", "ddd-test", "-n", "other", "-s", signature];
        Assert.NotEqual(0, (await Run("ssh-keygen", otherNamespace, payload)).ExitCode);
        string otherKey = Path.Combine(root, "other key");
        await GenerateKey(otherKey);
        await File.WriteAllTextAsync(Allowed, "ddd-test " + await File.ReadAllTextAsync(otherKey + ".pub"));
        Assert.NotEqual(0, (await Run("ssh-keygen", verify, payload)).ExitCode);
        await File.WriteAllTextAsync(signature, "not an SSHSIG\n");
        Assert.NotEqual(0, (await Run("ssh-keygen", verify, payload)).ExitCode);
    }

    [Fact]
    public async Task SoftwareSignerRejectsSignatureFromUnexpectedKeyBeforeWritingStdout()
    {
        await SetupKey();
        string otherKey = Path.Combine(root, "unexpected key");
        await GenerateKey(otherKey);
        environment["DDD_COMPAT_PRIVATE_KEY"] = otherKey;
        var result = await Run(Signer, ["-Y", "sign", "-n", "git", "-f", Key + ".pub", "-"], [1, 2, 3]);
        Assert.Equal(SigningHost.SigningFailed, result.ExitCode);
        Assert.Empty(result.Output);
    }

    [Theory]
    [InlineData("reject", SigningHost.SigningFailed)]
    [InlineData("cancel", SigningHost.Cancelled)]
    public async Task TestRejectionAndCancellationFailWithoutSignature(string mode, int code)
    {
        environment["DDD_COMPAT_MODE"] = mode;
        var result = await Run(Signer, ["-Y", "sign", "-n", "git", "-f", Key + ".pub", "-"], [1]);
        Assert.Equal(code, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.False(File.Exists(Path.Combine(Capture, "payload.bin")));
    }

    [Fact]
    public async Task GitAbortsCommitWhenSigningFails()
    {
        await SetupKey();
        Success(await Git("init", "--initial-branch=main"));
        environment["DDD_COMPAT_MODE"] = "reject";
        var result = await Git("-c", "user.name=DDD Test", "-c", "user.email=ddd@example.invalid", "-c", "gpg.format=ssh",
            "-c", "user.signingkey=" + Key + ".pub", "-c", "gpg.ssh.program=" + Signer.Replace('\\', '/'),
            "commit", "--allow-empty", "-S", "-m", "must not be committed");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("DDD: signing failed", result.Error);
        Assert.NotEqual(0, (await Git("rev-parse", "--verify", "HEAD")).ExitCode);
    }

    [Fact]
    public async Task UnsupportedInvocationProducesNoFilesOrSignature()
    {
        var result = await Run(Signer, ["-Y", "sign", "-n", "other", "-f", Key + ".pub", "-"], [1]);
        Assert.Equal(SigningHost.UnsupportedInvocation, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.False(File.Exists(Path.Combine(Capture, "payload.bin")));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
