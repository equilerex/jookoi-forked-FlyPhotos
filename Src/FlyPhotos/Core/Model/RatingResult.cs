namespace FlyPhotos.Core.Model;

internal class RatingResult(bool success, string failMessage = "", int rating = 0, bool queued = false)
{
    public bool Success { get; } = success;
    public string FailMessage { get; } = failMessage;

    /// <summary>The rating on disk after the call (0-5), or the one about to be written when <see cref="Queued"/>.</summary>
    public int Rating { get; } = rating;

    /// <summary>A write for this photo was already running and will save this rating next; the disk is not updated yet.</summary>
    public bool Queued { get; } = queued;
}
