using Chernika.Domain;
using Chernika.Web.Services;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Правила строки формы «Индивидуальные полномочия»: осмысленные действия,
/// фильтры и счётчики.
/// <para>
/// Тест идёт по ВСЕМУ действующему каталогу, а не по одному-двум праву:
/// проверка «все права работают» на одном примере не означает ничего. Для
/// каждого кода строится весь набор сочетаний «роль разрешает / роль запрещает»
/// и «решения нет / разрешающее / запрещающее».
/// </para>
/// <para>
/// Ожидаемое действие выводится из итогового доступа независимо от проверки:
/// осмысленно ровно одно изменение доступа — противоположное текущему, —
/// плюс снятие решения, если оно задано.
/// </para>
/// </summary>
public class IndividualPermissionDecisionTests
{
    private static UserEffectivePermissionDto P(
        string code,
        bool grantedByRole,
        bool? grantedOverride,
        bool effective,
        string module = "ХК — узлы",
        string? name = null,
        string? description = null) => new()
    {
        Code = code,
        Module = module,
        Name = name ?? ("Право " + code),
        Description = description ?? ("Описание права " + code),
        GrantedByRole = grantedByRole,
        OverrideIsGranted = grantedOverride,
        IsEffective = effective,
        Source = "test",
    };

    // ── Осмысленные действия: все сочетания роли и решения ────────────────

    public static TheoryData<bool, bool?, bool> Combinations => new()
    {
        // роль разрешает
        { true, null, true },    // доступ разрешён ролью, решения нет
        { true, true, true },    // роль разрешает, решение разрешающее
        { true, false, false },  // роль разрешает, решение запрещающее
        // роль запрещает
        { false, null, false },  // доступ запрещён, решения нет
        { false, true, true },   // роль запрещает, решение разрешающее
        { false, false, false },  // роль запрещает, решение запрещающее
    };

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Actions_AlwaysSingleAccessChange_OppositeToCurrent(
        bool grantedByRole, bool? grantedOverride, bool effective)
    {
        var p = P("Some.Code", grantedByRole, grantedOverride, effective);

        var actions = IndividualPermissionDecision.ActionsFor(p);

        // Ровно одно изменение доступа, и оно противоположно текущему.
        if (effective)
        {
            Assert.True(actions.CanDeny);
            Assert.False(actions.CanGrant);
        }
        else
        {
            Assert.True(actions.CanGrant);
            Assert.False(actions.CanDeny);
        }

        Assert.NotEqual(actions.CanGrant, actions.CanDeny);
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Actions_RevokeOverride_ShownOnlyWhenDecisionExists(
        bool grantedByRole, bool? grantedOverride, bool effective)
    {
        var p = P("Some.Code", grantedByRole, grantedOverride, effective);

        var actions = IndividualPermissionDecision.ActionsFor(p);

        Assert.Equal(grantedOverride != null, actions.CanRevokeOverride);
    }

    [Fact]
    public void Actions_ForRowGrantedByRole_GrantIsNotOffered()
    {
        // Разбирает исходную жалобу: право уже разрешено ролью, решения нет,
        // итоговый доступ разрешён. «Разрешить» здесь меняет ровно ничего.
        var p = P(PermissionCodes.HKView, grantedByRole: true, grantedOverride: null, effective: true);

        var actions = IndividualPermissionDecision.ActionsFor(p);

        Assert.False(actions.CanGrant);
        Assert.True(actions.CanDeny);
        Assert.False(actions.CanRevokeOverride);
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Actions_NeverEmpty(bool grantedByRole, bool? grantedOverride, bool effective)
    {
        // Действие по изменению доступа есть всегда: у строки есть осмысленный
        // исход в обе стороны.
        var actions = IndividualPermissionDecision.ActionsFor(
            P("Some.Code", grantedByRole, grantedOverride, effective));

        Assert.True(actions.Any);
    }

    // ── Правило проходит по всему действующему каталогу ─────────────────

    [Fact]
    public void ActiveCatalog_EveryPermissionMatches_SixRoleAndOverrideCombinations()
    {
        var active = PermissionCatalog.All
            .Where(d => IndividualPermissionDecision.IsActive(d.Code))
            .ToList();

        Assert.NotEmpty(active);

        foreach (var def in active)
        {
            // TheoryData перечисляется как object[], поэтому сочетание
            // разбирается по индексам, а не деконструкцией.
            foreach (var row in Combinations)
            {
                var grantedByRole = (bool)row[0];
                var grantedOverride = (bool?)row[1];
                var effective = (bool)row[2];

                var p = P(def.Code, grantedByRole, grantedOverride, effective,
                    module: def.Module, name: def.Name, description: def.Description);

                var actions = IndividualPermissionDecision.ActionsFor(p);

                Assert.True(actions.CanDeny || actions.CanGrant);
                Assert.False(actions.CanGrant && actions.CanDeny);
                Assert.Equal(grantedOverride != null, actions.CanRevokeOverride);
                Assert.True(actions.Any);
            }
        }
    }

    [Fact]
    public void ActiveCatalog_EveryPermissionIsSearchableByItsOwnFields()
    {
        // Поиск обещан по названию, описанию, модулю и коду. Проверяется на
        // реальном каталоге: если у права пустое описание, обещание перестаёт
        // выполняться, и это должно быть видно тестом, а не пользователю.
        foreach (var def in PermissionCatalog.All.Where(d => IndividualPermissionDecision.IsActive(d.Code)))
        {
            var p = P(def.Code, def.Code == def.Code, null, false,
                module: def.Module, name: def.Name, description: def.Description);

            Assert.True(IndividualPermissionDecision.MatchesSearch(p, def.Name), def.Code);
            Assert.True(IndividualPermissionDecision.MatchesSearch(p, def.Module), def.Code);
            Assert.True(IndividualPermissionDecision.MatchesSearch(p, def.Code), def.Code);

            if (!string.IsNullOrWhiteSpace(def.Description))
                Assert.True(IndividualPermissionDecision.MatchesSearch(p, def.Description), def.Code);
        }
    }

    [Fact]
    public void ActiveCatalog_EveryPermissionKeepsUsefulDescription()
    {
        // На действующем каталоге описание убирается только если оно дословно
        // повторяет название — тогда в строке нечего терять.
        foreach (var def in PermissionCatalog.All.Where(d => IndividualPermissionDecision.IsActive(d.Code)))
        {
            var p = P(def.Code, true, null, true, def.Module, def.Name, def.Description);

            var repeats = string.Equals(def.Description.Trim(), def.Name.Trim(), StringComparison.Ordinal);

            Assert.Equal(!repeats && !string.IsNullOrWhiteSpace(def.Description),
                IndividualPermissionDecision.NeedsDescription(p));
        }
    }

    [Fact]
    public void NeedsDescription_HidesOnlyEmptyOrRepeatingDescription()
    {
        Assert.False(IndividualPermissionDecision.NeedsDescription(
            P("C", true, null, true, description: "")));
        Assert.False(IndividualPermissionDecision.NeedsDescription(
            P("C", true, null, true, description: "   ")));
        Assert.False(IndividualPermissionDecision.NeedsDescription(
            P("C", true, null, true, name: "Одно и то же", description: "Одно и то же")));
        Assert.True(IndividualPermissionDecision.NeedsDescription(
            P("C", true, null, true, name: "Просмотр", description: "Просмотр реестра ХК")));
    }

    // ── Фильтры ─────────────────────────────────────────────────────────

    [Fact]
    public void Matches_FiltersByModuleAndStatusTogether()
    {
        var catalog = new List<UserEffectivePermissionDto>
        {
            P("A", grantedByRole: true, null, effective: true, module: "ХК — узлы"),
            P("B", grantedByRole: false, null, effective: false, module: "ХК — узлы"),
            P("C", grantedByRole: false, true, effective: true, module: "Справочники"),
            P("D", grantedByRole: true, false, effective: false, module: "Справочники"),
        };

        Assert.Equal(4, Visible(catalog, null, null, null).Count);
        Assert.Equal(2, Visible(catalog, null, "ХК — узлы", null).Count);
        Assert.Equal(2, Visible(catalog, null, null, IndividualPermissionDecision.StatusEffective).Count);
        Assert.Equal(2, Visible(catalog, null, null, IndividualPermissionDecision.StatusDenied).Count);
        Assert.Equal(2, Visible(catalog, null, null, IndividualPermissionDecision.StatusOverride).Count);

        // Модуль и статус вместе: пересечение, а не объединение.
        Assert.Equal(
            new[] { "A" },
            Visible(catalog, null, "ХК — узлы", IndividualPermissionDecision.StatusEffective).Select(x => x.Code));
        Assert.Equal(
            new[] { "D" },
            Visible(catalog, null, "Справочники", IndividualPermissionDecision.StatusDenied).Select(x => x.Code));
    }

    [Fact]
    public void Matches_SearchNarrowsByEveryPromisedField()
    {
        var catalog = new List<UserEffectivePermissionDto>
        {
            P("HK.View", true, null, true, "ХК — узлы", "Просмотр химмотологических карт", "Просмотр реестра"),
            P("Ref.View", true, null, true, "Справочники", "Просмотр справочников", "Просмотр значений"),
        };

        Assert.Equal(2, Visible(catalog, "Просмотр", null, null).Count);
        Assert.Equal(new[] { "HK.View" }, Visible(catalog, "HK.View", null, null).Select(x => x.Code));
        Assert.Equal(new[] { "Ref.View" }, Visible(catalog, "Справочник", null, null).Select(x => x.Code));
        Assert.Equal(new[] { "HK.View" }, Visible(catalog, "реестра", null, null).Select(x => x.Code));
        Assert.Empty(Visible(catalog, "несуществующее", null, null));
    }

    [Fact]
    public void Matches_ConservedPermissionsNeverVisible()
    {
        var catalog = PermissionCatalog.All
            .Select(d => P(d.Code, true, null, true, d.Module, d.Name, d.Description))
            .ToList();

        var visible = Visible(catalog, null, null, null).Select(x => x.Code).ToList();

        foreach (var code in PermissionCatalog.ConservedIndividualCardCodes)
            Assert.DoesNotContain(code, visible);

        Assert.DoesNotContain(
            visible,
            c => PermissionCatalog.All
                .First(d => d.Code == c).Module == PermissionCatalog.ConservedIndividualCardModule);
    }

    [Fact]
    public void Matches_SearchFindsConservedPermissionOnlyOutsideActiveForm()
    {
        // Право ИК исключено из активной формы всегда, даже когда искомое по
        // имени: иначе его можно было бы открыть поиском.
        var catalog = PermissionCatalog.All
            .Select(d => P(d.Code, true, null, true, d.Module, d.Name, d.Description))
            .ToList();

        Assert.Empty(Visible(catalog, "Индивидуальные карты", null, null));
        Assert.Empty(Visible(catalog, PermissionCatalog.ConservedIndividualCardModule, null, null));
    }

    // ── Счётчики ────────────────────────────────────────────────────────

    [Fact]
    public void Counters_FollowSearchAndModuleAndStatus()
    {
        var catalog = new List<UserEffectivePermissionDto>
        {
            P("A", grantedByRole: true, null, effective: true, module: "ХК — узлы"),
            P("B", grantedByRole: false, null, effective: false, module: "ХК — узлы"),
            P("C", grantedByRole: false, true, effective: true, module: "Справочники"),
            P("D", grantedByRole: true, false, effective: false, module: "Справочники"),
            P("E", grantedByRole: true, null, effective: true, module: "Справочники"),
        };

        var all = IndividualPermissionDecision.Count(catalog, null, null, null);
        Assert.Equal(new IndividualPermissionDecision.Counters(5, 3, 2, 2), all);

        // Выбор модуля сразу меняет «Всего» — без перезагрузки страницы.
        var module = IndividualPermissionDecision.Count(catalog, null, "ХК — узлы", null);
        Assert.Equal(new IndividualPermissionDecision.Counters(2, 1, 1, 0), module);

        var search = IndividualPermissionDecision.Count(catalog, "Справочник", null, null);
        Assert.Equal(3, search.Total);

        // Счётчики согласованы с фильтром статуса: при «Разрешено» «Запрещено»
        // закономерно ноль.
        var effective = IndividualPermissionDecision.Count(
            catalog, null, null, IndividualPermissionDecision.StatusEffective);
        Assert.Equal(0, effective.Denied);
        Assert.Equal(effective.Total, effective.Granted);

        var denied = IndividualPermissionDecision.Count(
            catalog, null, null, IndividualPermissionDecision.StatusDenied);
        Assert.Equal(0, denied.Granted);
        Assert.Equal(denied.Total, denied.Denied);

        var withOverride = IndividualPermissionDecision.Count(
            catalog, null, null, IndividualPermissionDecision.StatusOverride);
        Assert.Equal(2, withOverride.Total);
        Assert.Equal(2, withOverride.WithOverride);

        // Сброс фильтров возвращает общие значения.
        Assert.Equal(all, IndividualPermissionDecision.Count(catalog, "", "", ""));
    }

    [Fact]
    public void Counters_ExcludeConservedPermissions()
    {
        var catalog = PermissionCatalog.All
            .Select(d => P(d.Code, true, null, true, d.Module, d.Name, d.Description))
            .ToList();

        var counters = IndividualPermissionDecision.Count(catalog, null, null, null);

        Assert.Equal(catalog.Count - PermissionCatalog.ConservedIndividualCardCodes.Count, counters.Total);
        Assert.True(counters.Total > 0);
    }

    [Fact]
    public void Counters_CountWholeFilteredSelection_NotWhatFitsOnScreen()
    {
        // Счётчик считает ВСЮ отфильтрованную выборку. Ограничения видимых
        // строк нет и быть не должно: список прокручивается, поэтому «Всего» —
        // это сколько прав под фильтром, а не сколько поместилось в рабочую
        // область. 30 прав и 12 строк на экране — это «Всего: 30», а не 12.
        var catalog = new List<UserEffectivePermissionDto>
        {
            P("A", true, null, true, "ХК — узлы"),
            P("B", false, null, false, "ХК — узлы"),
            P("C", false, true, true, "Справочники"),
            P("D", true, false, false, "Справочники"),
        };

        foreach (var module in new[] { null, "ХК — узлы", "Справочники" })
        foreach (var status in new[]
                 {
                     null,
                     IndividualPermissionDecision.StatusEffective,
                     IndividualPermissionDecision.StatusDenied,
                     IndividualPermissionDecision.StatusOverride,
                 })
        foreach (var search in new[] { null, "Право", "Справочник" })
        {
            // Выборка — все строки, попавшие под фильтры: прокрутка не отбрасывает
            // строки, поэтому их число и есть «Всего».
            var selection = Visible(catalog, search, module, status);
            var counters = IndividualPermissionDecision.Count(catalog, search, module, status);

            Assert.Equal(selection.Count, counters.Total);
            Assert.Equal(selection.Count(x => x.IsEffective), counters.Granted);
            Assert.Equal(selection.Count(x => !x.IsEffective), counters.Denied);
            Assert.Equal(selection.Count(x => x.OverrideIsGranted != null), counters.WithOverride);
        }
    }

    [Fact]
    public void Counters_ReportWholeSelection_EvenWhenItExceedsWhatFitsOnScreen()
    {
        // Прямая проверка требования: прав больше, чем помещается в рабочую
        // область модалки, и «Всего» обязано называть полное число.
        const int totalRights = 30;
        const int fitsOnScreen = 12;

        var catalog = Enumerable.Range(1, totalRights)
            .Select(i => P("Code." + i, grantedByRole: true, null, effective: true, module: "ХК — узлы"))
            .ToList();

        var counters = IndividualPermissionDecision.Count(catalog, null, null, null);

        Assert.True(totalRights > fitsOnScreen, "условие теста: прав должно быть больше, чем помещается");
        Assert.Equal(totalRights, counters.Total);
        Assert.Equal(totalRights, counters.Granted);
        Assert.Equal(0, counters.Denied);
        Assert.Equal(0, counters.WithOverride);

        // Фильтр уменьшает «Всего» до размера выборки — тоже целиком, без
        // усечения до числа строк на экране.
        var filtered = IndividualPermissionDecision.Count(catalog, "Code.1", null, null);
        Assert.Equal(11, filtered.Total); // Code.1 и Code.10…Code.19
        Assert.Equal(11, Visible(catalog, "Code.1", null, null).Count);
    }

    [Fact]
    public void Counters_EmptyAndNullCatalog_AreZero()
    {
        Assert.Equal(new IndividualPermissionDecision.Counters(0, 0, 0, 0),
            IndividualPermissionDecision.Count(null, null, null, null));
        Assert.Equal(new IndividualPermissionDecision.Counters(0, 0, 0, 0),
            IndividualPermissionDecision.Count(Array.Empty<UserEffectivePermissionDto>(), "x", "y", "z"));
    }

    private static List<UserEffectivePermissionDto> Visible(
        IEnumerable<UserEffectivePermissionDto> catalog, string? search, string? module, string? status) =>
        catalog.Where(p => IndividualPermissionDecision.Matches(p, search, module, status)).ToList();
}