namespace ReproApp.Tests;

/// <summary>
/// Lets the noise class know when the hammer is done, so the CPU competition covers exactly the
/// window that matters instead of a guessed duration.
/// </summary>
internal static class ReproSignals
{
    private static readonly TaskCompletionSource Finished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static Task HammerFinished => Finished.Task;

    public static void SignalHammerFinished() => Finished.TrySetResult();
}
