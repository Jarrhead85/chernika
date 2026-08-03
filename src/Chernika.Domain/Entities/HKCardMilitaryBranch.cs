namespace Chernika.Domain.Entities;

public class HKCardMilitaryBranch
{
    public Guid HKCardId { get; set; }
    public HKCard HKCard { get; set; } = null!;

    public Guid MilitaryBranchId { get; set; }
    public MilitaryBranch MilitaryBranch { get; set; } = null!;
}
