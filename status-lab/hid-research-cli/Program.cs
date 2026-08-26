using Vorotex.K15.HidResearchLab;

namespace Vorotex.K15.HidResearch.Cli;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 2;
    private const int ExitReservedMode = 3;
    private const int ExitFailure = 10;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || Has(args, "--help") || Has(args, "-h"))
            {
                PrintHelp();
                return ExitOk;
            }

            if (Has(args, "--list-modes"))
            {
                foreach (var mode in HidResearchHeadless.SupportedModes) Console.WriteLine(mode);
                Console.WriteLine($"{HidResearchHeadless.ReservedNextMode} (reserved; not implemented)");
                return ExitOk;
            }

            var mode = Value(args, "--mode");
            var a = Value(args, "--a");
            var b = Value(args, "--b");
            var output = Value(args, "--out");

            if (mode is null || a is null || b is null || output is null)
            {
                Console.Error.WriteLine("ERROR: --mode, --a, --b and --out are required.");
                PrintHelp();
                return ExitUsage;
            }

            if (string.Equals(mode, HidResearchHeadless.ReservedNextMode, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"ERROR: mode '{mode}' is reserved for the next analyzer increment and is not implemented yet.");
                return ExitReservedMode;
            }

            if (!File.Exists(a))
            {
                Console.Error.WriteLine($"ERROR: input A does not exist: {a}");
                return ExitUsage;
            }

            if (!File.Exists(b))
            {
                Console.Error.WriteLine($"ERROR: input B does not exist: {b}");
                return ExitUsage;
            }

            var result = HidResearchHeadless.Run(mode, a, b, output);
            Console.WriteLine($"MODE={result.Mode}");
            Console.WriteLine($"VERDICT={result.Verdict}");
            Console.WriteLine($"JSON={result.JsonPath}");
            Console.WriteLine($"TEXT={result.TextPath}");
            return ExitOk;
        }
        catch (NotSupportedException ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return ExitReservedMode;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return ExitUsage;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return ExitUsage;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
            return ExitFailure;
        }
    }

    private static bool Has(string[] args, string token) =>
        args.Any(x => string.Equals(x, token, StringComparison.OrdinalIgnoreCase));

    private static string? Value(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) continue;
            return i + 1 < args.Length ? args[i + 1] : null;
        }
        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("VOROTEX K15 HID Research CLI");
        Console.WriteLine("Static read-only developer tool. It does not open/query/write HID devices and does not launch OEM applications.");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  Vorotex.K15.HidResearch.Cli --mode <mode> --a <VOROTEX exe> --b <SXS-W909 exe> --out <directory>");
        Console.WriteLine("  Vorotex.K15.HidResearch.Cli --list-modes");
        Console.WriteLine();
        Console.WriteLine("Implemented modes:");
        foreach (var mode in HidResearchHeadless.SupportedModes) Console.WriteLine("  " + mode);
        Console.WriteLine();
        Console.WriteLine($"Reserved next mode: {HidResearchHeadless.ReservedNextMode}");
    }
}
