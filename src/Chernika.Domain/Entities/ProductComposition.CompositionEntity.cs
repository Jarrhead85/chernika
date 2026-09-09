using Chernika.Domain.Models;

namespace Chernika.Domain.Entities;

public partial class ProductComposition : ICompositionEntity
{
    Guid ICompositionEntity.ObjectId => EquipmentModelId;
}
