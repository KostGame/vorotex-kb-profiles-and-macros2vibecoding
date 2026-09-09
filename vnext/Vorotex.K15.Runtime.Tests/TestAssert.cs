namespace Vorotex.K15.Runtime.Tests;

internal static class TestAssert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void False(bool condition, string message)
    {
        True(!condition, message);
    }

    public static void NotNull<T>(T? value, string message)
    {
        if (value is null)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}; expected '{expected}', actual '{actual}'");
        }
    }

    public static async Task ThrowsAsync(Func<Task> action, string message)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
