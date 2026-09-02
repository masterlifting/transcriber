using System.Text.Json.Serialization;

namespace LocalTranscriber.Recorder.Recording;

/// <summary>
/// session.json model for a two-track recording session.
/// </summary>
public sealed class SessionMetadata
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "recorded";

    [JsonPropertyName("error")]
    public ErrorInfo? Error { get; set; }

    [JsonPropertyName("startedAtUtc")]
    public string StartedAtUtc { get; set; } = "";

    [JsonPropertyName("stoppedAtUtc")]
    public string StoppedAtUtc { get; set; } = "";

    [JsonPropertyName("microphone")]
    public TrackInfo? Microphone { get; set; }

    [JsonPropertyName("remote")]
    public TrackInfo? Remote { get; set; }

    /// <summary>Start offset in milliseconds: positive = mic started first.</summary>
    [JsonPropertyName("startOffsetMicMinusRemoteMs")]
    public double StartOffsetMicMinusRemoteMs { get; set; }

    public sealed class ErrorInfo
    {
        [JsonPropertyName("component")]
        public string Component { get; set; } = "";

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("hresult")]
        public string? HResult { get; set; }
    }

    public sealed class TrackInfo
    {
        [JsonPropertyName("device")]
        public string Device { get; set; } = "";

        [JsonPropertyName("deviceId")]
        public string? DeviceId { get; set; }

        [JsonPropertyName("process")]
        public string? Process { get; set; }

        [JsonPropertyName("pid")]
        public int? Pid { get; set; }

        [JsonPropertyName("file")]
        public string File { get; set; } = "";

        [JsonPropertyName("format")]
        public string Format { get; set; } = "";

        [JsonPropertyName("durationSeconds")]
        public double DurationSeconds { get; set; }

        [JsonPropertyName("bytes")]
        public long Bytes { get; set; }

        [JsonPropertyName("firstPacketAtUtc")]
        public string FirstPacketAtUtc { get; set; } = "";
    }
}
