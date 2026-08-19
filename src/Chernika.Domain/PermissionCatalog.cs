namespace Chernika.Domain;

public sealed record PermissionDefinition(
    string Code,
    string Module,
    string Name,
    string Description,
    int SortOrder);

public static class PermissionCatalog
{
    public static IReadOnlyList<PermissionDefinition> All { get; } = new List<PermissionDefinition>
    {
        new(PermissionCodes.HKView, "Химмотологические карты", "Просмотр химмотологических карт", "Просмотр реестра и деталей химмотологических карт всех уровней", 1),
        new(PermissionCodes.HKNodeCreate, "ХК — узлы", "Создание ХК узла", "Создание новой химмотологической карты узла", 2),
        new(PermissionCodes.HKNodeEditDraft, "ХК — узлы", "Редактирование черновика ХК узла", "Изменение содержимого черновика химмотологической карты узла", 3),
        new(PermissionCodes.HKNodeSubmit, "ХК — узлы", "Отправка ХК узла на проверку", "Отправка заполненной химмотологической карты узла на нормоконтроль", 4),
        new(PermissionCodes.HKAggregateCreate, "ХК — агрегаты", "Создание ХК агрегата", "Создание новой химмотологической карты агрегата", 5),
        new(PermissionCodes.HKAggregateEditDraft, "ХК — агрегаты", "Редактирование черновика ХК агрегата", "Изменение содержимого черновика химмотологической карты агрегата", 6),
        new(PermissionCodes.HKAggregateSubmit, "ХК — агрегаты", "Отправка ХК агрегата на проверку", "Отправка заполненной химмотологической карты агрегата на нормоконтроль", 7),
        new(PermissionCodes.HKEquipmentCreate, "ХК — изделия", "Создание ХК изделия", "Создание новой химмотологической карты изделия", 8),
        new(PermissionCodes.HKEquipmentEditDraft, "ХК — изделия", "Редактирование черновика ХК изделия", "Изменение содержимого черновика химмотологической карты изделия", 9),
        new(PermissionCodes.HKEquipmentSubmit, "ХК — изделия", "Отправка ХК изделия на проверку", "Отправка заполненной химмотологической карты изделия на нормоконтроль", 10),
        new(PermissionCodes.HKComplexCreate, "ХК — комплексы", "Создание ХК комплекса", "Создание новой химмотологической карты комплекса", 11),
        new(PermissionCodes.HKComplexEditDraft, "ХК — комплексы", "Редактирование черновика ХК комплекса", "Изменение содержимого черновика химмотологической карты комплекса", 12),
        new(PermissionCodes.HKComplexSubmit, "ХК — комплексы", "Отправка ХК комплекса на проверку", "Отправка заполненной химмотологической карты комплекса на нормоконтроль", 13),
        new(PermissionCodes.HKReview, "Химмотологические карты", "Проверка и возврат на доработку", "Проверка химмотологической карты и возврат автору при необходимости", 14),
        new(PermissionCodes.HKApprove, "Химмотологические карты", "Утверждение ХК", "Утверждение проверенной химмотологической карты", 15),
        new(PermissionCodes.HKArchive, "Химмотологические карты", "Архивирование утверждённой ХК", "Позволяет вручную перевести утверждённую ХК в архив при обязательном указании причины и утверждённой заменяющей ХК.", 16),
        new(PermissionCodes.HKDeleteDraft, "Химмотологические карты", "Удаление черновика ХК", "Позволяет закрыть ХК в статусе «Черновик» с обязательным указанием причины.", 17),
        new(PermissionCodes.HKDeleteRevisionRequired, "Химмотологические карты", "Удаление ХК на доработке", "Позволяет закрыть ХК в статусе «На доработке» с обязательным указанием причины.", 170),
        new(PermissionCodes.HKDeleteOnReview, "Химмотологические карты", "Удаление ХК на проверке", "Нестандартное разрешение: позволяет закрыть ХК в статусе «На проверке».", 173),
        new(PermissionCodes.HKAttachmentView, "Химмотологические карты", "Просмотр PDF-вложений ХК", "Просмотр метаданных и скачивание PDF-сканов химмотологических карт", 171),
        new(PermissionCodes.HKAttachmentEdit, "Химмотологические карты", "Управление PDF-вложениями ХК", "Загрузка, замена и удаление PDF-сканов химмотологических карт", 172),
        new(PermissionCodes.ReferenceView, "Справочники", "Просмотр справочников", "Просмотр справочников узлов, агрегатов, комплектов, ГСМ и моделей техники", 18),
        new(PermissionCodes.ReferenceEdit, "Справочники", "Редактирование справочников", "Создание, изменение и удаление элементов справочников", 19),
        new(PermissionCodes.CompositionView, "Конструктивные составы", "Просмотр конструктивных составов", "Просмотр конструктивных составов изделий, агрегатов и комплексов", 20),
        new(PermissionCodes.CompositionEdit, "Конструктивные составы", "Редактирование конструктивных составов", "Создание, изменение и удаление конструктивных составов", 21),
        new(PermissionCodes.IndividualCardView, "Индивидуальные карты", "Просмотр индивидуальных карт", "Просмотр индивидуальных норм ГСМ по экземплярам техники", 22),
        new(PermissionCodes.IndividualCardGenerate, "Индивидуальные карты", "Формирование индивидуальных карт", "Автоматическое формирование индивидуальных карт на основе ХК", 23),
        new(PermissionCodes.ReportExport, "Отчёты", "Экспорт отчётов", "Экспорт отчётов и реестров в форматы PDF и XLSX", 24),
        new(PermissionCodes.TaskViewOwn, "Задачи", "Просмотр своих задач", "Просмотр задач, назначенных текущему пользователю", 25),
        new(PermissionCodes.TaskManage, "Задачи", "Управление задачами", "Выполнение, удаление и управление всеми задачами", 26),
        new(PermissionCodes.TaskView, "Задачи", "Просмотр задач", "Просмотр реестра и карточки задач в пределах доступа", 251),
        new(PermissionCodes.TaskAssign, "Задачи", "Назначение задач", "Создание и переназначение задач исполнителям", 252),
        new(PermissionCodes.TaskComplete, "Задачи", "Завершение задач", "Завершение и взятие в работу задач", 253),
        new(PermissionCodes.TaskCancel, "Задачи", "Отмена задач", "Отмена ошибочно созданных задач", 254),
        new(PermissionCodes.NotificationView, "Уведомления", "Просмотр уведомлений", "Просмотр своих уведомлений в колокольчике и на странице уведомлений", 255),
        new(PermissionCodes.NotificationMarkRead, "Уведомления", "Пометка уведомлений прочитанными", "Пометка своих уведомлений прочитанными", 256),
        new(PermissionCodes.AuditView, "Аудит", "Просмотр журнала аудита", "Просмотр журнала действий пользователей в системе", 27),
        new(PermissionCodes.UsersManage, "Администрирование", "Управление пользователями", "Создание, редактирование, блокировка и удаление учётных записей", 28),
        new(PermissionCodes.PermissionsManage, "Администрирование", "Управление дополнительными полномочиями", "Выдача, запрет и отмена индивидуальных полномочий пользователям", 29),
        new(PermissionCodes.SystemConfig, "Администрирование", "Системные настройки", "Доступ к системным настройкам и диагностике", 30),
    };

    private static readonly Dictionary<string, PermissionDefinition> ByCode = All.ToDictionary(x => x.Code);

    public static PermissionDefinition? FindByCode(string code) =>
        ByCode.TryGetValue(code, out var def) ? def : null;

    public static IReadOnlyList<PermissionDefinition> GetByModule(string module) =>
        All.Where(x => x.Module == module).ToList();

    public static IReadOnlyList<string> GetAllModules() =>
        All.Select(x => x.Module).Distinct().ToList();
}
