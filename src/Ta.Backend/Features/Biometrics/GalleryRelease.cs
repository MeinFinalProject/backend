namespace Ta.Backend.Features.Biometrics;

// Exact immutable wire bytes keep the strong ETag stable across requests.
public sealed class GalleryRelease
{
    public long GalleryReleaseId { get; set; }
    public string GalleryVersion { get; set; } = "";
    public string GalleryModelSha256 { get; set; } = "";
    public string GalleryEtag { get; set; } = "";
    public string GalleryDocument { get; set; } = "";
    public int GalleryTemplateCount { get; set; }
    public DateTimeOffset GalleryPublishedAt { get; set; }
}
