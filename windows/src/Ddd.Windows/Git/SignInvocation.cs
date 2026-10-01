namespace Ddd.Windows.Git;

public sealed record SignInvocation(string PublicKeyPath, string InputPath, bool Quiet)
{
    public static SignInvocation Parse(IReadOnlyList<string> args)
    {
        string? operation = null, signingNamespace = null, key = null, input = null;
        bool quiet = false;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "-Y":
                    SetOnce(ref operation, Value(args, ref i));
                    break;
                case "-n":
                    SetOnce(ref signingNamespace, Value(args, ref i));
                    break;
                case "-f":
                    SetOnce(ref key, Value(args, ref i));
                    break;
                case "-q" when !quiet:
                    quiet = true;
                    break;
                case string path when input is null && i == args.Count - 1 && !string.IsNullOrWhiteSpace(path) && (path == "-" || !path.StartsWith('-')):
                    input = path;
                    break;
                default:
                    throw new ArgumentException("Unsupported Git signing invocation. Only -Y sign -n git -f <public-key-path> [-q] <file|-> is supported.");
            }
        }
        if (operation != "sign" || signingNamespace != "git" || string.IsNullOrWhiteSpace(key) || input is null)
            throw new ArgumentException("Expected -Y sign -n git -f <public-key-path> [-q] <file|->.");
        return new(key, input, quiet);
    }

    private static string Value(IReadOnlyList<string> args, ref int index)
    {
        if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException("Missing signing option value.");
        return args[index];
    }

    private static void SetOnce(ref string? target, string value)
    {
        if (target is not null)
            throw new ArgumentException("Duplicate signing option.");
        target = value;
    }
}
