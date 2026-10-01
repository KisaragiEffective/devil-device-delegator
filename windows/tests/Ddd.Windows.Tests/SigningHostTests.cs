using System.Text;
using Ddd.Windows.Git;
using Xunit;

namespace Ddd.Windows.Tests;

public class SigningHostTests
{
    private static readonly string[] Arguments = ["-Y", "sign", "-n", "git", "-f", "public key.pub", "-"];

    [Fact]
    public void ParsesOnlySupportedSurface()
    {
        Assert.Equal(new SignInvocation("public key.pub", "-", false), SignInvocation.Parse(Arguments));
        Assert.True(SignInvocation.Parse(["-q", .. Arguments]).Quiet);
        string[][] bad = [[], ["-Y", "verify"], ["-Y", "sign", "-n", "other", "-f", "key", "-"],
            ["-Y", "sign", "-n", "git", "-f", "key", "file", "extra"], ["-Y", "sign", "-n", "git", "-f"],
            ["-U", .. Arguments], ["-n", "git", .. Arguments], [.. Arguments, "extra"],
            ["-q", "-q", .. Arguments]];
        foreach (string[] args in bad) Assert.Throws<ArgumentException>(() => SignInvocation.Parse(args));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileInputUsesExactBytesAndNeverOverwritesExistingSignature(bool existingSignature)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ddd-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "payload ü 署名");
            byte[] payloadBytes = [0xff, 0, 13, 10];
            byte[] response = [1, 2, 3];
            await File.WriteAllBytesAsync(path, payloadBytes);
            if (existingSignature) await File.WriteAllBytesAsync(path + ".sig", [9]);
            using var input = new MemoryStream([8]);
            using var output = new MemoryStream();
            using var error = new StringWriter();
            int code = await SigningHost.RunAsync([.. Arguments[..^1], path], input, output, error,
                (_, payload, _) => { Assert.Equal(payloadBytes, payload.ToArray()); return Task.FromResult(response); });
            Assert.Equal(existingSignature ? SigningHost.SigningFailed : 0, code);
            Assert.Equal(existingSignature ? new byte[] { 9 } : response, await File.ReadAllBytesAsync(path + ".sig"));
            Assert.Equal(0, input.Position);
            Assert.Empty(output.ToArray());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task PayloadAtExactLimitIsAccepted()
    {
        using var output = new MemoryStream();
        using var error = new StringWriter();
        int code = await SigningHost.RunAsync(Arguments, new MemoryStream(new byte[SigningHost.MaximumPayloadBytes]), output, error,
            (_, bytes, _) => { Assert.Equal(SigningHost.MaximumPayloadBytes, bytes.Length); return Task.FromResult(new byte[] { 1 }); });
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task PreservesExactBytesWithoutTextConversion()
    {
        byte[] bytes = [0, 0xff, 0xc3, 0x28, 13, 10, 10, 0];
        byte[] signature = Encoding.ASCII.GetBytes("test backend response\n");
        using var output = new MemoryStream();
        using var error = new StringWriter();
        int calls = 0;
        int code = await SigningHost.RunAsync(Arguments, new MemoryStream(bytes), output, error,
            (_, payload, _) => { calls++; Assert.Equal(bytes, payload.ToArray()); return Task.FromResult(signature); });
        Assert.Equal(0, code);
        Assert.Equal(1, calls);
        Assert.Equal(signature, output.ToArray());
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(SigningHost.MaximumPayloadBytes + 1)]
    public async Task EmptyAndOversizedInputNeverReachSigner(int size)
    {
        using var output = new MemoryStream();
        using var error = new StringWriter();
        int calls = 0;
        int code = await SigningHost.RunAsync(Arguments, new MemoryStream(new byte[size]), output, error,
            (_, _, _) => { calls++; return Task.FromResult(new byte[] { 1 }); });
        Assert.Equal(SigningHost.SigningFailed, code);
        Assert.Equal(0, calls);
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task UnsupportedInvocationDoesNotReadInputOrSign()
    {
        using var input = new MemoryStream([1, 2]);
        using var output = new MemoryStream();
        using var error = new StringWriter();
        int code = await SigningHost.RunAsync(["-Y", "verify"], input, output, error,
            (_, _, _) => throw new Exception("Must not sign."));
        Assert.Equal(SigningHost.UnsupportedInvocation, code);
        Assert.Equal(0, input.Position);
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task CancellationAndBackendFailureReturnNoSignatureOrSecret()
    {
        foreach (bool cancel in new[] { true, false })
        {
            using var output = new MemoryStream();
            using var error = new StringWriter();
            int code = await SigningHost.RunAsync(Arguments, new MemoryStream([1]), output, error,
                (_, _, _) => cancel ? throw new OperationCanceledException() : throw new InvalidOperationException("SECRET"));
            Assert.Equal(cancel ? SigningHost.Cancelled : SigningHost.SigningFailed, code);
            Assert.Empty(output.ToArray());
            Assert.DoesNotContain("SECRET", error.ToString());
        }
    }

    private sealed class StalledInput : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
    }

    [Fact]
    public async Task DeadlineInterruptsEvenAnInputThatIgnoresCancellation()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        using var input = new StalledInput();
        using var output = new MemoryStream();
        using var error = new StringWriter();
        int code = await SigningHost.RunAsync(Arguments, input, output, error,
            (_, _, _) => throw new Exception("Must not sign."), cancel.Token).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SigningHost.Cancelled, code);
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task CancellationAfterSigningDoesNotPublishResponse()
    {
        using var cancel = new CancellationTokenSource();
        using var output = new MemoryStream();
        using var error = new StringWriter();
        int code = await SigningHost.RunAsync(Arguments, new MemoryStream([1]), output, error,
            (_, _, _) => { cancel.Cancel(); return Task.FromResult(new byte[] { 1 }); }, cancel.Token);
        Assert.Equal(SigningHost.Cancelled, code);
        Assert.Empty(output.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16385)]
    public async Task InvalidResponseSizeIsNotPublished(int size)
    {
        using var output = new MemoryStream();
        using var error = new StringWriter();
        int code = await SigningHost.RunAsync(Arguments, new MemoryStream([1]), output, error,
            (_, _, _) => Task.FromResult(new byte[size]));
        Assert.Equal(SigningHost.SigningFailed, code);
        Assert.Empty(output.ToArray());
    }
}
