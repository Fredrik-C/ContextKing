using ContextKing.Cli.Commands;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    PrintHelp();
    return 0;
}

return args[0] switch
{
    "index"             => await IndexCommand.RunAsync(args[1..]),
    "find-scope"        => await FindScopeCommand.RunAsync(args[1..]),
    "search"            => await SearchCommand.RunAsync(args[1..]),
    "signatures"        => await SignaturesCommand.RunAsync(args[1..]),
    "get-method-source" => await GetMethodSourceCommand.RunAsync(args[1..]),
    "--version"         => PrintVersion(),
    _ => PrintError($"Unknown command: '{args[0]}'. Run 'ck --help' for usage.")
};

static void PrintHelp()
{
    Console.WriteLine("""
        ck — Context King: semantic code navigation for large C# and TypeScript codebases

        Commands:
          ck index              Build or update the semantic source-map index
          ck find-scope         Semantic search to find the most relevant folder(s)
          ck search             Scoped keyword search (semantic ranking + git grep)
          ck signatures         Extract method signatures from C#/TypeScript files (always live)
          ck get-method-source  Extract method/property source with exact span (always live)

        Run 'ck <command> --help' for command-specific options.

        Version: 1.2.5
        """);
}

static int PrintVersion()
{
    Console.WriteLine("ck 1.2.5");
    return 0;
}

static int PrintError(string message)
{
    Console.Error.WriteLine($"[ck] Error: {message}");
    return 1;
}
