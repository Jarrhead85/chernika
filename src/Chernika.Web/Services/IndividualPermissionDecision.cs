using Chernika.Domain;

namespace Chernika.Web.Services;

/// <summary>
/// Правила строки формы «Индивидуальные полномочия»: что показывать в строке,
/// что попадает под фильтры и что показывают счётчики.
/// <para>
/// Вынесено из разметки <c>Users.razor</c> намеренно. Правило «какие кнопки
/// осмысленны» — это требование к интерфейсу, и проверить его можно только
/// вызовом кода: разметку тест не читает, а проверка глазами по таблице
/// доказывает только то, что таблица нарисовалась.
/// </para>
/// <para>
/// Серверная модель прав при этом не меняется: код полномочия остаётся в DTO и
/// уходит в запрос при сохранении, меняется только то, что показывается.
/// </para>
/// </summary>
public static class IndividualPermissionDecision
{
    /// <summary>Фильтр «статус»: итоговый доступ разрешён.</summary>
    public const string StatusEffective = "effective";

    /// <summary>Фильтр «статус»: итоговый доступ запрещён.</summary>
    public const string StatusDenied = "denied";

    /// <summary>Фильтр «статус»: задано индивидуальное решение.</summary>
    public const string StatusOverride = "override";

    /// <summary>
    /// Осмысленные действия строки.
    /// <para>
    /// «Разрешить» и «Запретить» одновременно не бывает: осмысленно ровно одно
    /// изменение доступа, и оно противоположно текущему. Снятие решения — третье,
    /// отдельное действие.
    /// </para>
    /// </summary>
    public readonly record struct RowActions(bool CanGrant, bool CanDeny, bool CanRevokeOverride)
    {
        public bool Any => CanGrant || CanDeny || CanRevokeOverride;
    }

    /// <summary>Счётчики по текущей отфильтрованной выборке.</summary>
    public readonly record struct Counters(int Total, int Granted, int Denied, int WithOverride);

    /// <summary>
    /// Относится ли право к действующему функционалу.
    /// <para>
    /// В активную форму не попадают два вида кодов. Законсервированные права ИК:
    /// модуля нет в интерфейсе и право ничего бы не разрешило. Устаревшие общие
    /// коды (Task.Manage, Composition.Edit): своей операции у них нет, и выдача
    /// такого права вводила бы в заблуждение — администратор выдавал бы право,
    /// которое ничем не управляет. В обоих случаях код и данные сохраняются,
    /// исключается только возможность выдать его из формы.
    /// </para>
    /// </summary>
    public static bool IsActive(string code) =>
        !PermissionCatalog.IsConserved(code) && !PermissionCatalog.IsDeprecated(code);

    /// <summary>
    /// Действия строки — по ИТОГОВОМУ доступу, а не по одному полю «По роли».
    /// <para>
    /// Раньше кнопки определялись только через <c>OverrideIsGranted == null</c>,
    /// и у строки, уже разрешённой ролью, показывалось «Разрешить
    /// дополнительно» — действие, которое ничего не меняет: индивидуальное
    /// разрешение поверх разрешения роли не даёт большего доступа.
    /// </para>
    /// <para>
    /// Снятие индивидуального решения не путается с записью противоположного
    /// override: оно возвращает доступ по роли, а не назначает новый.
    /// </para>
    /// </summary>
    public static RowActions ActionsFor(UserEffectivePermissionDto p)
    {
        if (p == null) throw new ArgumentNullException(nameof(p));

        var hasOverride = p.OverrideIsGranted != null;

        return p.IsEffective
            ? new RowActions(CanGrant: false, CanDeny: true, CanRevokeOverride: hasOverride)
            : new RowActions(CanGrant: true, CanDeny: false, CanRevokeOverride: hasOverride);
    }

    /// <summary>Показывать ли описание полномочия в строке.</summary>
    public static bool NeedsDescription(UserEffectivePermissionDto p)
    {
        if (p == null) throw new ArgumentNullException(nameof(p));

        // Описание остаётся: в каталоге нет двух прав с одинаковым названием, и
        // именно оно объясняет разницу между «Создание ХК узла» и «Редактирование
        // черновика ХК узла». Убирается только тогда, когда смысла не добавляет —
        // пустое или дословно повторяющее название.
        return !string.IsNullOrWhiteSpace(p.Description)
            && !string.Equals(p.Description.Trim(), p.Name.Trim(), StringComparison.Ordinal);
    }

    /// <summary>Совпадение с поиском: название, описание, модуль или код.</summary>
    public static bool MatchesSearch(UserEffectivePermissionDto p, string? search)
    {
        if (p == null) throw new ArgumentNullException(nameof(p));
        if (string.IsNullOrEmpty(search)) return true;

        return p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || p.Code.Contains(search, StringComparison.OrdinalIgnoreCase)
            || p.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
            || p.Module.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Совпадение с фильтром модуля.</summary>
    public static bool MatchesModule(UserEffectivePermissionDto p, string? module)
    {
        if (p == null) throw new ArgumentNullException(nameof(p));
        return string.IsNullOrEmpty(module) || p.Module == module;
    }

    /// <summary>Совпадение с фильтром статуса.</summary>
    public static bool MatchesStatus(UserEffectivePermissionDto p, string? status)
    {
        if (p == null) throw new ArgumentNullException(nameof(p));
        if (string.IsNullOrEmpty(status)) return true;

        return status switch
        {
            StatusEffective => p.IsEffective,
            StatusDenied => !p.IsEffective,
            StatusOverride => p.OverrideIsGranted != null,
            _ => false,
        };
    }

    /// <summary>Попадает ли право в видимую выборку формы.</summary>
    public static bool Matches(
        UserEffectivePermissionDto p,
        string? search,
        string? module,
        string? status)
    {
        if (!IsActive(p.Code)) return false;

        return MatchesSearch(p, search)
            && MatchesModule(p, module)
            && MatchesStatus(p, status);
    }

    /// <summary>
    /// Счётчики по ОТФИЛЬТРОВАННОЙ выборке, а не по всему каталогу.
    /// <para>
    /// Раньше счётчики считали по каталогу: выбор модуля менял «Всего» только
    /// после обновления страницы, а поиск и фильтр статуса с ними расходились.
    /// </para>
    /// <para>
    /// Фильтр статуса в расчёт попадает: при «Разрешено» значение «Запрещено»
    /// закономерно становится нулём — это и означает согласованность счётчиков с
    /// фильтром. Законсервированные права ИК исключены: их нельзя выдать через
    /// эту форму, поэтому в счётчиках им не место.
    /// </para>
    /// </summary>
    public static Counters Count(
        IEnumerable<UserEffectivePermissionDto>? catalog,
        string? search,
        string? module,
        string? status)
    {
        if (catalog == null) return new Counters(0, 0, 0, 0);

        int total = 0, granted = 0, denied = 0, withOverride = 0;

        foreach (var p in catalog)
        {
            if (!IsActive(p.Code)) continue;
            if (!MatchesSearch(p, search)) continue;
            if (!MatchesModule(p, module)) continue;
            if (!MatchesStatus(p, status)) continue;

            total++;
            if (p.IsEffective) granted++;
            else denied++;
            if (p.OverrideIsGranted != null) withOverride++;
        }

        return new Counters(total, granted, denied, withOverride);
    }
}