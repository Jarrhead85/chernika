namespace Chernika.Domain;

public static class PermissionCodes
{
    public const string HKView = "HK.View";
    public const string HKNodeCreate = "HK.Node.Create";
    public const string HKNodeEditDraft = "HK.Node.EditDraft";
    public const string HKNodeSubmit = "HK.Node.Submit";

    public const string HKAggregateCreate = "HK.Aggregate.Create";
    public const string HKAggregateEditDraft = "HK.Aggregate.EditDraft";
    public const string HKAggregateSubmit = "HK.Aggregate.Submit";

    public const string HKEquipmentCreate = "HK.Equipment.Create";
    public const string HKEquipmentEditDraft = "HK.Equipment.EditDraft";
    public const string HKEquipmentSubmit = "HK.Equipment.Submit";

    public const string HKComplexCreate = "HK.Complex.Create";
    public const string HKComplexEditDraft = "HK.Complex.EditDraft";
    public const string HKComplexSubmit = "HK.Complex.Submit";

    public const string HKReview = "HK.Review";
    public const string HKApprove = "HK.Approve";
    public const string HKArchive = "HK.Archive";
    public const string HKDeleteDraft = "HK.Delete.Draft";
    public const string HKDeleteOnReview = "HK.Delete.OnReview";
    public const string HKDeleteRevisionRequired = "HK.Delete.RevisionRequired";
    public const string HKAttachmentView = "HK.Attachment.View";
    public const string HKAttachmentEdit = "HK.Attachment.Edit";

    public const string ReferenceView = "Reference.View";
    public const string ReferenceEdit = "Reference.Edit";
    public const string CompositionView = "Composition.View";
    public const string CompositionEdit = "Composition.Edit";

    public const string IndividualCardView = "IndividualCard.View";
    public const string IndividualCardGenerate = "IndividualCard.Generate";
    public const string ReportExport = "Report.Export";

    public const string TaskView = "Task.View";
    public const string TaskAssign = "Task.Assign";
    public const string TaskComplete = "Task.Complete";
    public const string TaskCancel = "Task.Cancel";
    public const string NotificationView = "Notification.View";
    public const string NotificationMarkRead = "Notification.MarkRead";

    public const string TaskViewOwn = "Task.ViewOwn";
    public const string TaskManage = "Task.Manage";
    public const string AuditView = "Audit.View";
    public const string UsersManage = "Users.Manage";
    public const string PermissionsManage = "Permissions.Manage";
    public const string SystemConfig = "System.Config";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        HKView, HKNodeCreate, HKNodeEditDraft, HKNodeSubmit,
        HKAggregateCreate, HKAggregateEditDraft, HKAggregateSubmit,
        HKEquipmentCreate, HKEquipmentEditDraft, HKEquipmentSubmit,
        HKComplexCreate, HKComplexEditDraft, HKComplexSubmit,
        HKReview, HKApprove, HKArchive, HKDeleteDraft, HKDeleteOnReview, HKDeleteRevisionRequired,
        HKAttachmentView, HKAttachmentEdit,
        ReferenceView, ReferenceEdit, CompositionView, CompositionEdit,
        IndividualCardView, IndividualCardGenerate, ReportExport,
        TaskView, TaskAssign, TaskComplete, TaskCancel,
        NotificationView, NotificationMarkRead,
        TaskViewOwn, TaskManage, AuditView,
        UsersManage, PermissionsManage, SystemConfig,
    };
}
