using Chernika.Domain.Enums;

namespace Chernika.Domain.Models;

public record CreateCompositionRequest(Guid EquipmentModelId, string? Comment);

public record UpdateCompositionDraftRequest(Guid Id, string? Comment, DateTime? EffectiveDate, DateTime? ExpirationDate);

public record AddPartRequest(Guid CompositionId, string Name, string? Description, int SortOrder);

public record UpdatePartRequest(Guid PartId, string Name, string? Description, int SortOrder);

public record AddNodeRequest(Guid PartId, Guid NodeId, int Quantity);

public record UpdateNodeQuantityRequest(Guid NodeId, int Quantity);

public record ChangeCompositionStatusRequest(Guid CompositionId, ProductCompositionStatus NewStatus, string? Comment);
