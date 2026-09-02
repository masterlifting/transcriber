namespace Transcriber.Core;

public sealed class UsageException : Exception
{
    public UsageException(string message) : base(message) { }
}

public sealed class DependencyException : Exception
{
    public DependencyException(string message) : base(message) { }
}

public sealed class RecordingException : Exception
{
    public RecordingException(string message) : base(message) { }
}

public sealed class TranscriptionException : Exception
{
    public TranscriptionException(string message) : base(message) { }
}
