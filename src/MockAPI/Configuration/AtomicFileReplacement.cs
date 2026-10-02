namespace MockAPI.Configuration;

internal static class AtomicFileReplacement
{
    private const int MaximumAttempts = 4;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    public static Task ReplaceAsync(
        string temporaryPath,
        string targetPath,
        CancellationToken cancellationToken) =>
        ReplaceAsync(
            temporaryPath,
            targetPath,
            static (source, destination) => File.Move(source, destination, overwrite: true),
            static (delay, token) => Task.Delay(delay, token),
            cancellationToken);

    internal static async Task ReplaceAsync(
        string temporaryPath,
        string targetPath,
        Action<string, string> replace,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(replace);
        ArgumentNullException.ThrowIfNull(delay);

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                replace(temporaryPath, targetPath);
                return;
            }
            catch (IOException) when (attempt < MaximumAttempts)
            {
            }
            catch (UnauthorizedAccessException) when (attempt < MaximumAttempts)
            {
            }

            // Windows scanners can briefly hold a flushed file between close and atomic replacement.
            await delay(RetryDelay, cancellationToken);
        }
    }
}
