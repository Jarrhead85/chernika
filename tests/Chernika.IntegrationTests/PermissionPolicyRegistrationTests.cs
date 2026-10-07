using Chernika.Domain;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Политики полномочий объявлены в одном месте, поэтому хосты не могут разойтись.
/// <para>
/// Раньше каждый хост объявлял политики сам, и списки разошлись: четыре политики
/// были только в Api, две только в Web. Из-за этого одно и то же право
/// операция считалось по-разному в зависимости от источника запроса. Тест
/// фиксирует состав списка, чтобы расхождение нельзя было ввести снова, не
/// заметив.
/// </para>
/// </summary>
public class PermissionPolicyRegistrationTests
{
    private static AuthorizationOptions Build()
    {
        var options = new AuthorizationOptions();
        options.AddPermissionPolicies();
        return options;
    }

    [Theory]
    [InlineData("ViewHK")]
    [InlineData("CreateHK")]
    [InlineData("EditHK")]
    [InlineData("SendToApprove")]
    [InlineData("VerifyHK")]
    [InlineData("ReturnHK")]
    [InlineData("ApproveHK")]
    [InlineData("ArchiveHK")]
    [InlineData("DeleteHK")]
    [InlineData("HKAttachmentView")]
    [InlineData("HKAttachmentEdit")]
    [InlineData("ViewReferences")]
    [InlineData("ManageReference")]
    [InlineData("CreateEquipment")]
    [InlineData("EditEquipment")]
    [InlineData("DeleteEquipment")]
    [InlineData("ManageCoefficients")]
    [InlineData("ViewComposition")]
    [InlineData("ViewTasks")]
    [InlineData("ViewAllTasks")]
    [InlineData("AssignTasks")]
    [InlineData("CompleteTasks")]
    [InlineData("CancelTasks")]
    [InlineData("ViewNotifications")]
    [InlineData("ReportExport")]
    [InlineData("ViewAuditLog")]
    [InlineData("ManageUsers")]
    [InlineData("ManageRoles")]
    [InlineData("SystemConfig")]
    [InlineData("CreateIndividualCard")]
    [InlineData("DeleteIndividualCard")]
    [InlineData("ManageIndividualCards")]
    public void Policy_IsRegistered_AndRequiresAtLeastOnePermission(string policyName)
    {
        var policy = Build().GetPolicy(policyName);

        Assert.NotNull(policy);
        Assert.Contains(policy!.Requirements, r => r is PermissionRequirement);

        // Пустое требование означало бы политику, которая не проверяет ничего:
        // маршрут выглядел бы защищённым, а пропускал бы всех.
        var requirement = Assert.IsType<PermissionRequirement>(policy.Requirements.First());
        Assert.NotEmpty(requirement.PermissionCodes);
    }

    [Fact]
    public void RetiredBroadPolicies_AreGone()
    {
        var options = Build();

        // Task.Manage и Composition.Edit выведены из активного контура по решению
        // владельца: своей операции у них нет. Политики ManageTasks и
        // ManageComposition удалены вместе с ними — иначе широкий код снова
        // обошёл бы индивидуальный запрет конкретного действия.
        Assert.Null(options.GetPolicy("ManageTasks"));
        Assert.Null(options.GetPolicy("ManageComposition"));
    }

    [Fact]
    public void GranularTaskPolicies_RequireOnlyTheirOwnPermission()
    {
        var options = Build();

        // Каждое действие задач защищено своим правом, а не общим: широкое
        // разрешение обнуляло бы индивидуальный запрет конкретного действия.
        AssertCodes(options, "AssignTasks", PermissionCodes.TaskAssign);
        AssertCodes(options, "CompleteTasks", PermissionCodes.TaskComplete);
        AssertCodes(options, "CancelTasks", PermissionCodes.TaskCancel);
        AssertCodes(options, "ViewAllTasks", PermissionCodes.TaskView);

        // ViewTasks — только свои задачи, и это отдельное право.
        AssertCodes(options, "ViewTasks", PermissionCodes.TaskViewOwn);
    }

    [Fact]
    public void CompositionPolicy_RequiresView_NotDeprecatedCode()
    {
        // Допуск к операциям состава — по Composition.View. Широкий код
        // Composition.Edit в условии обошёл бы индивидуальный запрет
        // гранулярного действия; точное право проверяет сервис.
        AssertCodes(Build(), "ViewComposition", PermissionCodes.CompositionView);
    }

    [Fact]
    public void BothHosts_CanResolveEveryPolicyName_UsedByCode()
    {
        // Список имён, на которые ссылается код. Раньше половина этих имён
        // отсутствовала в одном из хостов, и это обнаруживалось только в отчёте.
        var referenced = new[]
        {
            "ArchiveHK", "AssignTasks", "CancelTasks", "CompleteTasks", "CreateEquipment",
            "CreateHK", "CreateIndividualCard", "DeleteEquipment", "DeleteHK",
            "DeleteIndividualCard", "EditEquipment", "EditHK", "HKAttachmentEdit",
            "HKAttachmentView", "ManageCoefficients", "ManageRoles", "ManageUsers",
            "ReportExport", "SystemConfig", "ViewAllTasks", "ViewAuditLog",
            "ViewComposition", "ViewHK", "ViewNotifications", "ViewReferences", "ViewTasks",
        };

        var options = Build();

        foreach (var name in referenced)
        {
            Assert.True(options.GetPolicy(name) != null, "политика не зарегистрирована: " + name);
        }
    }

    private static void AssertCodes(AuthorizationOptions options, string policyName, params string[] expected)
    {
        var policy = options.GetPolicy(policyName);
        Assert.NotNull(policy);

        var requirement = policy!.Requirements.OfType<PermissionRequirement>().Single();

        Assert.Equal(expected.OrderBy(x => x), requirement.PermissionCodes.OrderBy(x => x));
    }
}