namespace LocalTranscriber.Core.Operations;

/// <summary>Structured operation lifecycle used by both CLI and Desktop renderers.</summary>
public enum OperationStage
{
    Idle,
    Preparing,
    Recording,
    Stopping,
    Transcribing,
    Merging,
    Completed,
    Failed,
}

/// <summary>
/// Structured progress/state event. The CLI renders this as console text; the
/// Desktop renders it as UI state. Progress is null or indeterminate unless a
/// real percentage is known.
/// </summary>
public sealed record OperationProgress(OperationStage Stage, string Message, double? Progress = null);
