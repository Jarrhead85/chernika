namespace Chernika.Domain.Entities;

public enum ProposalTargetType
{
    Node = 0,
    AssemblyUnit = 1,
    GsmMaterial = 2
}

public enum ProposalStatus
{
    Pending = 0,
    Accepted = 1,
    Rejected = 2
}

public class ReferenceProposal
{
    public Guid Id { get; set; }
    public Guid HKCardId { get; set; }
    public HKCard HKCard { get; set; } = null!;
    public ProposalTargetType TargetType { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Gost { get; set; }
    public string? Type { get; set; }
    public ProposalStatus Status { get; set; } = ProposalStatus.Pending;
    public Guid? CreatedStubNodeId { get; set; }
    public Guid? CreatedStubAssemblyUnitId { get; set; }
    public Guid? CreatedStubGsmMaterialId { get; set; }
    public string CreatedByUserId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}
