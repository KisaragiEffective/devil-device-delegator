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
            Task write = WriteInputAsync(process, input, deadline.Token);
            await Task.WhenAll(write, readOutput, process.WaitForExitAsync(deadline.Token));
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

    private static async Task WriteInputAsync(Process process, byte[]? input, CancellationToken cancellationToken)
    {
        try
        {
            if (input is not null)
                await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken);
        }
        finally
        {
            process.StandardInput.Close();
        }
    }
}
