using Chernika.Domain.Models;

namespace Chernika.Domain.Entities;

public partial class AggregateComposition : ICompositionEntity
{
    Guid ICompositionEntity.ObjectId => AggregateId;
}
