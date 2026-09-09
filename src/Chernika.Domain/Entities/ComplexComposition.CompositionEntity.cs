using Chernika.Domain.Models;

namespace Chernika.Domain.Entities;

public partial class ComplexComposition : ICompositionEntity
{
    Guid ICompositionEntity.ObjectId => ComplexId;
}
