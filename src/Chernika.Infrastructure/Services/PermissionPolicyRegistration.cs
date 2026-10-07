using Chernika.Domain;
using Microsoft.AspNetCore.Authorization;

namespace Chernika.Infrastructure.Services;

/// <summary>
/// Единая регистрация полномочийных политик авторизации.
/// <para>
/// Раньше каждый хост объявлял политики сам, и списки разошлись: в Api были
/// CreateEquipment, EditEquipment, DeleteEquipment и DeleteIndividualCard, которых
/// не было в Web, а в Web — ViewReferences и ViewNotifications, которых не было в
/// Api. Из-за этого одно и то же право операция считалось по-разному в зависимости
/// от того, откуда пришёл запрос.
/// </para>
/// <para>
/// Объявление вынесено сюда, поэтому разойтись снова не может: оба хоста
/// вызывают один и тот же метод. Схема аутентификации хостов при этом не
/// меняется — добавляется только список политик.
/// </para>
/// </summary>
public static class PermissionPolicyRegistration
{
    /// <summary>
    /// Регистрирует все полномощные политики. Вызывается обоими хостами.
    /// </summary>
    public static AuthorizationOptions AddPermissionPolicies(this AuthorizationOptions options)
    {
        // ── Химмотологические карты ────────────────────────────────────
        options.AddPolicy("ViewHK", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.HKView)));

        // Создание и редактирование черновика разрешены на любом уровне ХК:
        // политика отвечает за допуск к операции, а конкретный уровень и
        // организация проверяет сервис.
        options.AddPolicy("CreateHK", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.HKNodeCreate, PermissionCodes.HKAggregateCreate,
            PermissionCodes.HKEquipmentCreate, PermissionCodes.HKComplexCreate)));

        options.AddPolicy("EditHK", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.HKNodeEditDraft, PermissionCodes.HKAggregateEditDraft,
            PermissionCodes.HKEquipmentEditDraft, PermissionCodes.HKComplexEditDraft)));

        options.AddPolicy("SendToApprove", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.HKNodeSubmit, PermissionCodes.HKAggregateSubmit,
            PermissionCodes.HKEquipmentSubmit, PermissionCodes.HKComplexSubmit)));

        options.AddPolicy("VerifyHK", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.HKReview)));

        options.AddPolicy("ReturnHK", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.HKReview)));

        options.AddPolicy("ApproveHK", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.HKApprove)));

        options.AddPolicy("ArchiveHK", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.HKArchive)));

        options.AddPolicy("DeleteHK", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.HKDeleteDraft, PermissionCodes.HKDeleteOnReview,
            PermissionCodes.HKDeleteRevisionRequired)));

        options.AddPolicy("HKAttachmentView", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.HKAttachmentView)));

        options.AddPolicy("HKAttachmentEdit", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.HKAttachmentEdit)));

        // ── Справочники ─────────────────────────────────────────────────
        options.AddPolicy("ViewReferences", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.ReferenceView)));

        options.AddPolicy("ManageReference", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.ReferenceEdit)));

        // CreateEquipment, EditEquipment и DeleteEquipment — это одна операция
        // «изменение справочника», разделённая по эндпоинтам. Отдельные имена
        // были источником расхождения хостов, но и перед удалением различали
        // создание, правку и удаление, поэтому имена сохранены.
        options.AddPolicy("CreateEquipment", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.ReferenceEdit)));

        options.AddPolicy("EditEquipment", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.ReferenceEdit)));

        options.AddPolicy("DeleteEquipment", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.ReferenceEdit)));

        options.AddPolicy("ManageCoefficients", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.ReferenceEdit)));

        // ── Конструктивные составы ─────────────────────────────────────
        // Допуск к операциям состава. Точное право действия проверяет сервис
        // (EquipmentService требует гранулярные права по уровню), поэтому здесь
        // достаточно права просмотра: широкий код Composition.Edit в условии
        // обошёл бы индивидуальный запрет конкретного действия.
        options.AddPolicy("ViewComposition", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.CompositionView)));

        // ── Задачи ──────────────────────────────────────────────────────
        // Политика на каждое действие по его проверке в TaskService. Общая
        // ManageTasks не используется: широкое право обошло бы индивидуальный
        // запрет конкретного действия.
        options.AddPolicy("ViewTasks", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.TaskViewOwn)));

        options.AddPolicy("ViewAllTasks", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.TaskView)));

        options.AddPolicy("AssignTasks", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.TaskAssign)));

        options.AddPolicy("CompleteTasks", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.TaskComplete)));

        options.AddPolicy("CancelTasks", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.TaskCancel)));

        // ── Уведомления, отчёты, аудит ──────────────────────────────────
        options.AddPolicy("ViewNotifications", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.NotificationView)));

        options.AddPolicy("ReportExport", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.ReportExport)));

        options.AddPolicy("ViewAuditLog", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.AuditView)));

        // ── Администрирование ───────────────────────────────────────────
        options.AddPolicy("ManageUsers", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.UsersManage)));

        options.AddPolicy("ManageRoles", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.PermissionsManage)));

        options.AddPolicy("SystemConfig", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.SystemConfig)));

        // ── Законсервированный модуль ИК ────────────────────────────────
        // Модуль выведен из действующего функционала. Политики остаются, потому
        // что эндпоинты ИК живы и должны отклонять запросы по тем же правилам,
        // что и раньше; выдавать эти права из активной формы нельзя.
        options.AddPolicy("CreateIndividualCard", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.IndividualCardGenerate)));

        options.AddPolicy("DeleteIndividualCard", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.IndividualCardGenerate)));

        options.AddPolicy("ManageIndividualCards", p => p.AddRequirements(new PermissionRequirement(
            PermissionCodes.IndividualCardGenerate)));

        return options;
    }
}