namespace Ta.Backend.Features.Biometrics;

public sealed class BiometricEnrollment
{
    public Guid BiometricEnrollmentId { get; set; } = Guid.NewGuid();
    public Guid StudentId { get; set; }
    public string BiometricEnrollmentStatus { get; set; } = "draft";
    public DateTimeOffset BiometricEnrollmentConsentedAt { get; set; } = DateTimeOffset.UtcNow;
    public string BiometricEnrollmentConsentVersion { get; set; } = "research-v1";
    public DateTimeOffset BiometricEnrollmentCreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string BiometricEnrollmentReviewNote { get; set; } = "";
}
public sealed class BiometricTemplate
{
    public Guid BiometricTemplateId { get; set; } = Guid.NewGuid();
    public Guid BiometricEnrollmentId { get; set; }
    public string BiometricTemplatePose { get; set; } = "";
    public string BiometricTemplateModelSha256 { get; set; } = "";
    public string BiometricTemplateImageSha256 { get; set; } = "";
    public byte[] BiometricTemplateEmbedding { get; set; } = [];
    public bool BiometricTemplateActive { get; set; }
    public double BiometricTemplateDetectionScore { get; set; }
    public double BiometricTemplateAlignmentError { get; set; }
    public DateTimeOffset BiometricTemplateCreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
