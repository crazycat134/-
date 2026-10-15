namespace ScreenReceiver.Contracts;

/// <summary>
/// Metadata describing a single captured frame.
/// The JPEG bytes themselves are carried separately.
/// </summary>
public sealed record FrameMetadata(
    long FrameId,
    DateTimeOffset CapturedAtUtc,
    int Width,
    int Height,
    string ContentType,
    long SizeBytes);
