using Ddd.Windows.Git;

using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
return await SigningHost.RunAsync(args, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error,
    (_, _, _) => throw new InvalidOperationException("iPhone signing backend is not implemented in the compatibility milestone."),
    cancellation.Token);
