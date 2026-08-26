namespace Chernika.Domain.Models;

public sealed class HKCardKpiDto
{
    public int Total { get; init; }
    public int Draft { get; init; }
    public int OnReview { get; init; }
    public int RevisionRequired { get; init; }
    public int Approved { get; init; }
    public int RequiresMyAction { get; init; }
}
