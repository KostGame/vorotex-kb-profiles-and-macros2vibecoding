using System.Text;
using System.Text.Json;

namespace Vorotex.K15.StatusLab;

internal static class EventJournal
{
    private const string MutexName = @"Local\VorotexK15StatusLabJournal";

    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VOROTEX",
        "K15 Status Lab");

    public static string FilePath { get; } = Path.Combine(DirectoryPath, "events.jsonl");

    public static void Append(object record)
    {
        Directory.CreateDirectory(DirectoryPath);
        var line = JsonSerializer.Serialize(record, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        using var mutex = new Mutex(false, MutexName);
        var locked = false;
        try
        {
            locked = mutex.WaitOne(TimeSpan.FromSeconds(5));
            if (!locked)
                throw new TimeoutException("Timed out waiting for the Status Lab journal lock.");

            File.AppendAllText(FilePath, line + Environment.NewLine, new UTF8Encoding(false));
        }
        finally
        {
            if (locked)
                mutex.ReleaseMutex();
        }
    }

    public static void EnsureExists()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (!File.Exists(FilePath))
            File.WriteAllText(FilePath, string.Empty, new UTF8Encoding(false));
    }

    public static void Clear()
    {
        Directory.CreateDirectory(DirectoryPath);
        using var mutex = new Mutex(false, MutexName);
        var locked = false;
        try
        {
            locked = mutex.WaitOne(TimeSpan.FromSeconds(5));
            if (!locked)
                throw new TimeoutException("Timed out waiting for the Status Lab journal lock.");

            File.WriteAllText(FilePath, string.Empty, new UTF8Encoding(false));
        }
        finally
        {
            if (locked)
                mutex.ReleaseMutex();
        }
    }
}
