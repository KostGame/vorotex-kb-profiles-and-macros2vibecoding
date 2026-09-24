using System.Text.Json;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: runtime-relocator-probe <resources-directory> <runtime-root>");
    return 64;
}

try
{
    var result = K15.CodexBridge.WindowsAdapter.Program.ResolveDesktopRuntimeFromResources(
        Path.GetFullPath(args[0]),
        Path.GetFullPath(args[1]));
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        path = result.Path,
        sha256 = result.Sha256Override
    }));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"{error.GetType().Name}:{error.Message}");
    return 2;
}
