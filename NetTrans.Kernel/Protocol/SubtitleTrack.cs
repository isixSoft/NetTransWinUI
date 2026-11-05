namespace NetTrans.Kernel.Protocol;

/// <summary>
/// A side subtitle track advertised by a streaming manifest (HLS EXT-X-MEDIA
/// or a DASH text adaptation set). The track points at an external subtitle
/// file that can be fetched as a plain download task; segmented subtitle
/// tracks (fMP4/WebVTT-in-segments) are not advertised yet.
/// </summary>
/// <param name="Name">Display label (HLS NAME / DASH representation id).</param>
/// <param name="Language">BCP-47 language tag when the manifest tells.</param>
/// <param name="Url">Absolute URL of the external subtitle file.</param>
/// <param name="IsDefault">The manifest marks this track as DEFAULT.</param>
public sealed record SubtitleTrack(string Name, string? Language, string Url, bool IsDefault);
