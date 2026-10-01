namespace Ddd.Windows.Git;

public static class SigningHost
{
    // Provisional P1 bound, not an interoperable protocol constant yet.
    public const int MaximumPayloadBytes = 1024 * 1024;
    public const int UnsupportedInvocation = 2;
    public const int SigningFailed = 3;
    public const int Cancelled = 130;

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args, Stream input, Stream output, TextWriter error,
        Func<SignInvocation, ReadOnlyMemory<byte>, CancellationToken, Task<byte[]>> sign,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var invocation = SignInvocation.Parse(args);
            using var fileInput = invocation.InputPath == "-" ? null : new FileStream(invocation.InputPath,
                FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            Stream payloadInput = fileInput ?? input;
            using var payload = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                int count = await payloadInput.ReadAsync(buffer, cancellationToken).AsTask().WaitAsync(cancellationToken);
                if (count == 0) break;
                if (payload.Length + count > MaximumPayloadBytes)
                    throw new InvalidDataException("Git signing payload exceeds the provisional 1 MiB limit.");
                await payload.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            if (payload.Length == 0)
                throw new InvalidDataException("Empty signing payload.");
            byte[] signature = await sign(invocation, payload.ToArray(), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (signature.Length == 0 || signature.Length > 16 * 1024)
                throw new InvalidDataException("Invalid signing response size.");
            if (invocation.InputPath == "-")
            {
                await output.WriteAsync(signature, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            else
            {
                string signaturePath = invocation.InputPath + ".sig";
                // Never overwrite an existing signature (or a pre-created link).
                using var fileOutput = new FileStream(signaturePath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.Asynchronous);
                try
                {
                    await fileOutput.WriteAsync(signature, cancellationToken);
                    await fileOutput.FlushAsync(cancellationToken);
                }
                catch
                {
                    fileOutput.Dispose();
                    File.Delete(signaturePath);
                    throw;
                }
            }
            return 0;
        }
        catch (ArgumentException)
        {
            await error.WriteLineAsync("DDD: unsupported Git signing invocation.");
            return UnsupportedInvocation;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("DDD: signing cancelled.");
            return Cancelled;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Do not include payload, paths, tokens or backend diagnostics in Git's stderr.
            await error.WriteLineAsync("DDD: signing failed; no signature returned.");
            return SigningFailed;
        }
    }
}
