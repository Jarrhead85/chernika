using System.ComponentModel.DataAnnotations;

namespace Chernika.Domain.Entities;

public class HKCardAttachment
{
    public Guid Id { get; set; }

    public Guid HKCardId { get; set; }
    public HKCard HKCard { get; set; } = null!;

    [Required, StringLength(255)]
    public string OriginalFileName { get; set; } = null!;

    [Required, StringLength(512)]
    public string StorageKey { get; set; } = null!;

    [Required, StringLength(100)]
    public string ContentType { get; set; } = "application/pdf";

    public long SizeBytes { get; set; }

    [Required, StringLength(128)]
    public string Sha256 { get; set; } = null!;

    [Required, StringLength(450)]
    public string UploadedByUserId { get; set; } = null!;

    public string? UploadedByUserName { get; set; }

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
