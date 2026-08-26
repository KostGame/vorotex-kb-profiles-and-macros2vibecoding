using System.Text;
using System.Text.Json;

namespace Vorotex.K15.HidResearchLab;

public sealed record HidResearchHeadlessResult(
    string Mode,
    string Verdict,
    string JsonPath,
    string TextPath);

public static class HidResearchHeadless
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static IReadOnlyList<string> SupportedModes { get; } =
    [
        "sleep-report",
        "sleep-report-construction",
        "sleep-payload-seed",
        "sleep-payload-helper-semantics"
    ];

    public static string ReservedNextMode => "sleep-payload-source";

    public static HidResearchHeadlessResult Run(string mode, string exeA, string exeB, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentException.ThrowIfNullOrWhiteSpace(exeA);
        ArgumentException.ThrowIfNullOrWhiteSpace(exeB);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var a = Path.GetFullPath(exeA);
        var b = Path.GetFullPath(exeB);
        var output = Path.GetFullPath(outputDirectory);

        if (!File.Exists(a)) throw new FileNotFoundException("OEM executable A was not found.", a);
        if (!File.Exists(b)) throw new FileNotFoundException("OEM executable B was not found.", b);

        Directory.CreateDirectory(output);

        return mode.Trim().ToLowerInvariant() switch
        {
            "sleep-report" => RunSleepReport(a, b, output),
            "sleep-report-construction" => RunSleepReportConstruction(a, b, output),
            "sleep-payload-seed" => RunSleepPayloadSeed(a, b, output),
            "sleep-payload-helper-semantics" => RunSleepPayloadHelperSemantics(a, b, output),
            "sleep-payload-source" => throw new NotSupportedException(
                "Mode 'sleep-payload-source' is reserved for the next local research increment and is not implemented yet."),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode,
                "Unsupported research mode. Use the CLI --list-modes option.")
        };
    }

    private static HidResearchHeadlessResult RunSleepReport(string a, string b, string output)
    {
        var report = OemNdeviceAggregateCopyAnalyzer.AnalyzeKeyboardSleepReport(a, b);
        return Write(
            "sleep-report",
            "oem-keyboard-sleep-report-trace",
            report.Verdict,
            report,
            OemNdeviceAggregateCopyAnalyzer.KeyboardSleepReportToText(report),
            output);
    }

    private static HidResearchHeadlessResult RunSleepReportConstruction(string a, string b, string output)
    {
        var report = OemNdeviceAggregateCopyAnalyzer.AnalyzeKeyboardSleepReportConstruction(a, b);
        return Write(
            "sleep-report-construction",
            "oem-keyboard-sleep-report-construction",
            report.Verdict,
            report,
            OemNdeviceAggregateCopyAnalyzer.KeyboardSleepReportConstructionToText(report),
            output);
    }

    private static HidResearchHeadlessResult RunSleepPayloadSeed(string a, string b, string output)
    {
        var report = OemNdeviceAggregateCopyAnalyzer.AnalyzeKeyboardSleepReportPayloadSeed(a, b);
        return Write(
            "sleep-payload-seed",
            "oem-keyboard-sleep-report-payload-seed",
            report.Verdict,
            report,
            OemNdeviceAggregateCopyAnalyzer.KeyboardSleepReportPayloadSeedToText(report),
            output);
    }

    private static HidResearchHeadlessResult RunSleepPayloadHelperSemantics(string a, string b, string output)
    {
        var report = OemNdeviceAggregateCopyAnalyzer.AnalyzeKeyboardSleepPayloadHelperSemantics(a, b);
        return Write(
            "sleep-payload-helper-semantics",
            "oem-keyboard-sleep-payload-helper-semantics",
            report.Verdict,
            report,
            OemNdeviceAggregateCopyAnalyzer.KeyboardSleepPayloadHelperSemanticsToText(report),
            output);
    }

    private static HidResearchHeadlessResult Write<T>(
        string mode,
        string baseName,
        string verdict,
        T report,
        string text,
        string output)
    {
        var jsonPath = Path.Combine(output, baseName + ".json");
        var textPath = Path.Combine(output, baseName + ".txt");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false));
        File.WriteAllText(textPath, text, new UTF8Encoding(false));
        return new HidResearchHeadlessResult(mode, verdict, jsonPath, textPath);
    }
}
