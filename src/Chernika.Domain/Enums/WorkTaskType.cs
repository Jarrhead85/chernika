namespace Chernika.Domain.Enums;

public enum WorkTaskType
{
    HKReview = 1,
    HKRevision = 2,
    HKExpirationReview = 3,
    ParentHKRevision = 4,
    ReferenceProposalReview = 5,
    UserAdministration = 6,
    CompositionReview = 7,
    CompositionReadiness = 8,

    /// <summary>Рассмотрение этапа реквизитов и основания ХК.</summary>
    HKDetailsReview = 9,

    /// <summary>Рассмотрение этапа состава и норм ХК.</summary>
    HKCompositionReview = 10
}
