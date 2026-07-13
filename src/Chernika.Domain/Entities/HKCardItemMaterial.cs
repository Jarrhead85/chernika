using Chernika.Domain.Enums;

namespace Chernika.Domain.Entities;

public class HKCardItemMaterial
{
    public Guid Id { get; set; }
    public Guid HKCardItemId { get; set; }
    public HKCardItem HKCardItem { get; set; } = null!;
    public Guid GsmMaterialId { get; set; }
    public GsmMaterial GsmMaterial { get; set; } = null!;
    public GsmCategory Category { get; set; } = GsmCategory.Primary;
}
