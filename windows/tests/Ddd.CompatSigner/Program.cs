using System.Text;
using System.Text.Json;
using Ddd.CompatSigner;
using Ddd.Windows.Git;

// This executable is deliberately test-only: production never reads these variables or a private key.
if (args is ["--probe-exit-without-reading-stdin", "0" or "7"])
{
    Console.Out.Write("probe stdout");
    Console.Error.Write("probe stderr");
    return int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
}
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
string? capture = Environment.GetEnvironmentVariable("DDD_COMPAT_CAPTURE");
if (capture is not null)
{
    Directory.CreateDirectory(capture);
    await File.WriteAllTextAsync(Path.Combine(capture, "argv.json"), JsonSerializer.Serialize(args));
}
return await SigningHost.RunAsync(args, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error,
    async (invocation, payload, token) =>
    {
        if (Environment.GetEnvironmentVariable("DDD_COMPAT_MODE") == "reject")
            throw new InvalidOperationException("Test rejection.");
        if (Environment.GetEnvironmentVariable("DDD_COMPAT_MODE") == "cancel")
            throw new OperationCanceledException();
        string key = Environment.GetEnvironmentVariable("DDD_COMPAT_PRIVATE_KEY")
            ?? throw new InvalidOperationException("Test key not configured.");
        if (capture is not null)
            await File.WriteAllBytesAsync(Path.Combine(capture, "payload.bin"), payload.ToArray(), token);
        string publicKey = await File.ReadAllTextAsync(invocation.PublicKeyPath, token);
        string[] fields = publicKey.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2 || fields[0] != "ecdsa-sha2-nistp256")
            throw new InvalidDataException("Expected test P-256 public key.");
        var result = await Tool.RunAsync("ssh-keygen", ["-q", "-Y", "sign", "-n", "git", "-f", key, "-"],
            Environment.CurrentDirectory, payload.ToArray(), new Dictionary<string, string?> { ["SSH_AUTH_SOCK"] = null }, token);
        if (result.ExitCode != 0) throw new InvalidOperationException("Test OpenSSH signing failed.");
        string scratch = Path.Combine(Path.GetTempPath(), "ddd-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string allowed = Path.Combine(scratch, "allowed_signers");
            string signature = Path.Combine(scratch, "signature");
            await File.WriteAllTextAsync(allowed, $"ddd-test namespaces=\"git\" {fields[0]} {fields[1]}\n", new UTF8Encoding(false), token);
            await File.WriteAllBytesAsync(signature, result.Output, token);
            var verification = await Tool.RunAsync("ssh-keygen", ["-Y", "verify", "-f", allowed, "-I", "ddd-test", "-n", "git", "-s", signature],
                scratch, payload.ToArray(), cancellationToken: token);
            if (verification.ExitCode != 0) throw new InvalidOperationException("Test signature did not verify against expected key/payload/namespace.");
            return result.Output;
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }, cancellation.Token);
