using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public static class DbSeeder
{
    private static readonly (string UserName, string Password, string FullName, string Position, UserRole Role)[] TestUsers =
    [
        ("admin", "Admin@12345", "Администратор системы", "Системный администратор", UserRole.SystemAdmin),
        ("normadmin", "Norm@12345", "Нормировщик", "Нормировщик ГСМ", UserRole.NormAdmin),
        ("operator", "Op@12345", "А. Оператор", "Оператор-эксперт", UserRole.Operator),
        ("head", "Head@12345", "Начальник отдела", "Начальник отдела нормирования", UserRole.HeadOfDepartment),
        ("guest", "Guest@12345", "В. Гость", "Внешний наблюдатель", UserRole.Guest),
    ];

    private static readonly Dictionary<string, string[]> RolePermissions = new()
    {
        [nameof(UserRole.SystemAdmin)] = PermissionCodes.All.ToArray(),
        [nameof(UserRole.NormAdmin)] =
        [
            PermissionCodes.HKView,
            PermissionCodes.HKNodeCreate, PermissionCodes.HKNodeEditDraft, PermissionCodes.HKNodeSubmit,
            PermissionCodes.HKAggregateCreate, PermissionCodes.HKAggregateEditDraft, PermissionCodes.HKAggregateSubmit,
            PermissionCodes.HKEquipmentCreate, PermissionCodes.HKEquipmentEditDraft, PermissionCodes.HKEquipmentSubmit,
            PermissionCodes.HKComplexCreate, PermissionCodes.HKComplexEditDraft, PermissionCodes.HKComplexSubmit,
            PermissionCodes.HKReview, PermissionCodes.HKApprove, PermissionCodes.HKArchive, PermissionCodes.HKDelete,
            PermissionCodes.ReferenceView, PermissionCodes.ReferenceEdit,
            PermissionCodes.CompositionView, PermissionCodes.CompositionEdit,
            PermissionCodes.IndividualCardView, PermissionCodes.IndividualCardGenerate,
            PermissionCodes.ReportExport,
            PermissionCodes.AuditView,
        ],
        [nameof(UserRole.Operator)] =
        [
            PermissionCodes.HKView,
            PermissionCodes.HKNodeCreate, PermissionCodes.HKNodeEditDraft, PermissionCodes.HKNodeSubmit,
            PermissionCodes.ReferenceView,
            PermissionCodes.CompositionView,
            PermissionCodes.IndividualCardView, PermissionCodes.IndividualCardGenerate,
            PermissionCodes.ReportExport,
            PermissionCodes.TaskViewOwn,
        ],
        [nameof(UserRole.HeadOfDepartment)] =
        [
            PermissionCodes.HKView,
            PermissionCodes.ReferenceView,
            PermissionCodes.CompositionView,
            PermissionCodes.IndividualCardView,
            PermissionCodes.ReportExport,
            PermissionCodes.AuditView,
            PermissionCodes.TaskViewOwn,
        ],
        [nameof(UserRole.Guest)] =
        [
            PermissionCodes.HKView,
            PermissionCodes.ReferenceView,
            PermissionCodes.CompositionView,
            PermissionCodes.IndividualCardView,
        ],
    };

    public static async Task SeedAsync(AppDbContext db, UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager)
    {
        foreach (var roleName in Enum.GetNames<UserRole>())
        {
            if (!await roleManager.RoleExistsAsync(roleName))
                await roleManager.CreateAsync(new IdentityRole(roleName));
        }

        foreach (var (userName, password, fullName, position, role) in TestUsers)
        {
            var user = await userManager.FindByNameAsync(userName);
            if (user == null)
            {
                user = new ApplicationUser
                {
                    UserName = userName,
                    Email = $"{userName}@chernika.local",
                    FullName = fullName,
                    Position = position,
                    IsActive = true,
                    EmailConfirmed = true,
                };
                var result = await userManager.CreateAsync(user, password);
                if (!result.Succeeded)
                {
                    var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                    throw new InvalidOperationException($"Failed to create user {userName}: {errors}");
                }
            }
            if (!await userManager.IsInRoleAsync(user, role.ToString()))
                await userManager.AddToRoleAsync(user, role.ToString());
        }

        foreach (var (roleName, permissions) in RolePermissions)
        {
            foreach (var perm in permissions)
            {
                var exists = await db.RolePermissionTemplates.AnyAsync(x => x.RoleName == roleName && x.PermissionCode == perm);
                if (!exists)
                {
                    db.RolePermissionTemplates.Add(new RolePermissionTemplate
                    {
                        Id = Guid.NewGuid(),
                        RoleName = roleName,
                        PermissionCode = perm,
                    });
                }
            }
        }

        var branch = await db.Branches.OrderBy(b => b.Code).FirstOrDefaultAsync();
        if (branch == null)
        {
            branch = new Branch { Id = Guid.NewGuid(), Name = "Филиал №14", Code = "BR-014" };
            var branch2 = new Branch { Id = Guid.NewGuid(), Name = "Филиал №7", Code = "BR-007" };
            db.Branches.AddRange(branch, branch2);
        }
        else if (await db.Nodes.AnyAsync())
        {
            await AssignDefaultBranchAsync(userManager, branch.Id);
            return;
        }

        await AssignDefaultBranchAsync(userManager, branch.Id);

        var gsmMaterials = new List<GsmMaterial>
        {
            new() { Id = Guid.NewGuid(), Name = "МС-20", Type = "Моторное масло", Gost = "ГОСТ 21743-76" },
            new() { Id = Guid.NewGuid(), Name = "М-10Г2к", Type = "Моторное масло", Gost = "ГОСТ 8581-78" },
            new() { Id = Guid.NewGuid(), Name = "SAE 30", Type = "Моторное масло", Gost = "SAE J300" },
            new() { Id = Guid.NewGuid(), Name = "ТСп-15К", Type = "Трансмиссионное масло", Gost = "ГОСТ 23652-79" },
            new() { Id = Guid.NewGuid(), Name = "Литол-24", Type = "Пластичная смазка", Gost = "ГОСТ 21150-87" },
            new() { Id = Guid.NewGuid(), Name = "Mobil Delvac 1330", Type = "Моторное масло", Gost = "API CI-4" },
            new() { Id = Guid.NewGuid(), Name = "ОЖ-40", Type = "Охлаждающая жидкость", Gost = "ГОСТ 28084-89" },
            new() { Id = Guid.NewGuid(), Name = "ДТ-З", Type = "Топливо", Gost = "ГОСТ 305-2013" },
        };
        db.GsmMaterials.AddRange(gsmMaterials);

        var nodes = new List<Node>
        {
            new() { Id = Guid.NewGuid(), Code = "UZ-044", Name = "Двигатель ЯМЗ-238" },
            new() { Id = Guid.NewGuid(), Code = "UZ-102", Name = "Система охлаждения" },
            new() { Id = Guid.NewGuid(), Code = "UZ-018", Name = "Топливная система" },
            new() { Id = Guid.NewGuid(), Code = "UZ-011", Name = "Узел трения №4" },
            new() { Id = Guid.NewGuid(), Code = "UZ-077", Name = "Редуктор моста" },
        };
        db.Nodes.AddRange(nodes);

        var assemblyUnits = new List<AssemblyUnit>
        {
            new() { Id = Guid.NewGuid(), Code = "AS-001", Name = "Картер двигателя" },
            new() { Id = Guid.NewGuid(), Code = "AS-002", Name = "Масляный фильтр" },
            new() { Id = Guid.NewGuid(), Code = "AS-003", Name = "Топливный насос" },
            new() { Id = Guid.NewGuid(), Code = "AS-004", Name = "Радиатор" },
            new() { Id = Guid.NewGuid(), Code = "AS-005", Name = "Редуктор" },
        };
        db.AssemblyUnits.AddRange(assemblyUnits);

        var models = new List<EquipmentModel>
        {
            new() { Id = Guid.NewGuid(), Index = "4320-31", Name = "УРАЛ-4320", Type = "Колесная техника", Brand = "Урал", Modification = "4320-31" },
            new() { Id = Guid.NewGuid(), Index = "5350", Name = "КАМАЗ-5350", Type = "Колесная техника", Brand = "КамАЗ", Modification = "5350" },
        };
        db.EquipmentModels.AddRange(models);

        var coeffTypes = new List<CoefficientType>
        {
            new() { Id = Guid.NewGuid(), Name = "Сезонный", Group = CoefficientGroup.Seasonal, SortOrder = 1 },
            new() { Id = Guid.NewGuid(), Name = "Климатический", Group = CoefficientGroup.Climatic, SortOrder = 2 },
            new() { Id = Guid.NewGuid(), Name = "Территориальный", Group = CoefficientGroup.Territorial, SortOrder = 3 },
            new() { Id = Guid.NewGuid(), Name = "Режим эксплуатации", Group = CoefficientGroup.OperationMode, SortOrder = 4 },
        };
        db.CoefficientTypes.AddRange(coeffTypes);

        var coefficients = new List<Coefficient>
        {
            new() { Id = Guid.NewGuid(), CoefficientTypeId = coeffTypes[0].Id, Name = "Зимняя эксплуатация", ConditionDescription = "Температура ниже -20°C", Value = 1.10m, SortOrder = 1 },
            new() { Id = Guid.NewGuid(), CoefficientTypeId = coeffTypes[0].Id, Name = "Летняя эксплуатация", ConditionDescription = "Температура выше +25°C", Value = 1.05m, SortOrder = 2 },
            new() { Id = Guid.NewGuid(), CoefficientTypeId = coeffTypes[1].Id, Name = "Холодный регион", ConditionDescription = "Среднегодовая t < 0°C", Value = 1.08m, SortOrder = 1 },
            new() { Id = Guid.NewGuid(), CoefficientTypeId = coeffTypes[1].Id, Name = "Умеренный регион", ConditionDescription = "Среднегодовая t 0..+15°C", Value = 1.00m, SortOrder = 2 },
            new() { Id = Guid.NewGuid(), CoefficientTypeId = coeffTypes[2].Id, Name = "Базовый район", ConditionDescription = "Основная территория эксплуатации", Value = 1.00m, SortOrder = 1 },
            new() { Id = Guid.NewGuid(), CoefficientTypeId = coeffTypes[2].Id, Name = "Горная местность", ConditionDescription = "Высота над уровнем моря > 1500м", Value = 1.15m, SortOrder = 2 },
            new() { Id = Guid.NewGuid(), CoefficientTypeId = coeffTypes[3].Id, Name = "Штатный режим", ConditionDescription = "Стандартная эксплуатация", Value = 1.00m, SortOrder = 1 },
            new() { Id = Guid.NewGuid(), CoefficientTypeId = coeffTypes[3].Id, Name = "Усиленный режим", ConditionDescription = "Интенсивная эксплуатация", Value = 1.12m, SortOrder = 2 },
        };
        db.Coefficients.AddRange(coefficients);

        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "HK-2026-001301",
            Version = "v0426",
            Status = HKCardStatus.OnReview,
            BranchId = branch.Id,
            NodeId = nodes[0].Id,
            Purpose = "ХК является нормативной карточкой узла и задаёт перечень разрешённых марок ГСМ, а также базовые объёмы/массы для данного узла.",
            NormativeBasis = "ГОСТ 21743-76, ГОСТ 8581-78, SAE J300",
            Notes = "Базовая карта двигателя ЯМЗ-238",
            CreatedAt = DateTime.UtcNow.AddDays(-7),
            UpdatedAt = DateTime.UtcNow,
        };

        var items = new List<HKCardItem>();
        for (int i = 0; i < assemblyUnits.Count; i++)
        {
            var item = new HKCardItem
            {
                Id = Guid.NewGuid(),
                HKCardId = card.Id,
                AssemblyUnitId = assemblyUnits[i].Id,
                Quantity = i == 0 ? 1 : 2,
                Volume = i == 0 ? 2.400m : 1.100m,
                UnitOfMeasure = "кг",
                Periodicity = "ТО-2",
                SortOrder = i + 1,
            };
            items.Add(item);
        }

        foreach (var item in items)
        {
            var mat = new HKCardItemMaterial
            {
                Id = Guid.NewGuid(),
                HKCardItemId = item.Id,
                GsmMaterialId = gsmMaterials[0].Id,
                Category = GsmCategory.Primary,
            };
            db.HKCardItemMaterials.Add(mat);
        }

        card.Items = items;
        db.HKCards.Add(card);

        db.HKCardStatusLogs.Add(new HKCardStatusLog
        {
            Id = Guid.NewGuid(),
            HKCardId = card.Id,
            FromStatus = HKCardStatus.Draft,
            ToStatus = HKCardStatus.OnReview,
            ChangedByUserId = Guid.NewGuid(),
            Comment = "Создана и отправлена на проверку",
            ChangedAt = DateTime.UtcNow.AddDays(-5),
        });

        var card2 = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "HK-2026-001155",
            Version = "v0326",
            Status = HKCardStatus.RevisionRequired,
            BranchId = branch.Id,
            NodeId = nodes[1].Id,
            Purpose = "Карта системы охлаждения",
            NormativeBasis = "ГОСТ 28084-89",
            Notes = "Требует актуализации",
            CreatedAt = DateTime.UtcNow.AddDays(-30),
            UpdatedAt = DateTime.UtcNow,
        };
        db.HKCards.Add(card2);

        var card3 = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "HK-2026-001274",
            Version = "v0226",
            Status = HKCardStatus.Approved,
            BranchId = branch.Id,
            NodeId = nodes[2].Id,
            ApprovedDate = DateTime.UtcNow.AddDays(-60),
            Purpose = "Карта топливной системы",
            NormativeBasis = "ГОСТ 305-2013",
            CreatedAt = DateTime.UtcNow.AddDays(-90),
            UpdatedAt = DateTime.UtcNow.AddDays(-60),
        };
        db.HKCards.Add(card3);

        await db.SaveChangesAsync();
    }

    private static async Task AssignDefaultBranchAsync(UserManager<ApplicationUser> userManager, Guid branchId)
    {
        foreach (var userName in new[] { "normadmin", "operator" })
        {
            var user = await userManager.FindByNameAsync(userName);
            if (user != null && user.BranchId != branchId)
            {
                user.BranchId = branchId;
                await userManager.UpdateAsync(user);
            }
        }
    }
}
