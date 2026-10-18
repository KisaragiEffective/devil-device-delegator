using System.Diagnostics;

namespace Ddd.CompatSigner;

public sealed record ToolResult(int ExitCode, byte[] Output, string Error);

public static class Tool
{
    public static async Task<ToolResult> RunAsync(string executable, IEnumerable<string> args,
        string workingDirectory, byte[]? input = null, IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in args) start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start tool.");
        using var output = new MemoryStream();
        Task readOutput = process.StandardOutput.BaseStream.CopyToAsync(output, deadline.Token);
        Task<string> readError = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            Task<IOException?> write = WriteInputAsync(process, input, deadline.Token);
            await Task.WhenAll(write, readOutput, readError, process.WaitForExitAsync(deadline.Token));
            IOException? inputError = await write;
            // A rejecting child may exit without consuming stdin. Preserve its exit code
            // and diagnostics, but never treat incomplete input delivery as success.
            if (inputError is not null && process.ExitCode == 0)
                throw new IOException("Tool exited successfully before all supplied stdin could be delivered.", inputError);
            return new(process.ExitCode, output.ToArray(), await readError);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(readOutput, readError); } catch (OperationCanceledException) { }
            throw;
        }
    }

    private static async Task<IOException?> WriteInputAsync(Process process, byte[]? input, CancellationToken cancellationToken)
    {
        IOException? inputError = null;
        try
        {
            if (input is not null)
                await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken);
        }
        catch (IOException error)
        {
            inputError = error;
        }
        finally
        {
            try { process.StandardInput.Close(); }
            catch (IOException error) { inputError ??= error; }
        }
        return inputError;
    }
}
