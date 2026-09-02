namespace Transcriber.Core.Operations;

/// <summary>
/// OS-backed exclusive file lock for one call directory's transcription.
/// The lock is the open exclusive file handle (FileShare.None), so a stale
/// lock file never blocks a retry, and the lock is released automatically
/// when the owning process exits.
/// </summary>
public sealed class TranscriptionLock : IDisposable
{
    private readonly FileStream _stream;

    private TranscriptionLock(FileStream stream)
    {
        _stream = stream;
    }

    public static string LockPath(string callDir) => Path.Combine(callDir, ".transcribing.lock");

    /// <summary>Returns null when another process currently holds the lock.</summary>
    public static TranscriptionLock? TryAcquire(string callDir)
    {
        try
        {
            var stream = new FileStream(
                LockPath(callDir),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            return new TranscriptionLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool IsHeld(string callDir)
    {
        try
        {
            using var probe = new FileStream(
                LockPath(callDir),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            return false; // we could open it, so nobody holds it
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}
