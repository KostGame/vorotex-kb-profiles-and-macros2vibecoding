using System.Text;
using System.Text.Json;

var mode = Environment.GetEnvironmentVariable("FAKE_CHILD_MODE") ?? "unknown";
var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();

await using var error = Console.OpenStandardError();
var marker = Encoding.UTF8.GetBytes("fake-child:" + mode + "\n");
await error.WriteAsync(marker);

switch (mode)
{
    case "argv":
        await input.CopyToAsync(Stream.Null);
        var argv = JsonSerializer.Serialize(args);
        await output.WriteAsync(Encoding.UTF8.GetBytes(argv));
        return 0;

    case "echo":
        await input.CopyToAsync(output);
        return 0;

    case "exit":
        await input.CopyToAsync(Stream.Null);
        return int.TryParse(Environment.GetEnvironmentVariable("FAKE_CHILD_EXIT_CODE"), out var exitCode)
            ? exitCode
            : 64;

    case "early-exit":
        return int.TryParse(Environment.GetEnvironmentVariable("FAKE_CHILD_EXIT_CODE"), out var earlyExitCode)
            ? earlyExitCode
            : 37;

    case "close-stdout":
        await input.CopyToAsync(Stream.Null);
        output.Close();
        return 0;

    default:
        await input.CopyToAsync(Stream.Null);
        await error.WriteAsync(Encoding.UTF8.GetBytes("fake-child:unsupported-mode\n"));
        return 64;
}
