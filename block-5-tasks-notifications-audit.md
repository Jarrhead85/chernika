# Блок 5. Задачи, уведомления, аудит

## 0. Обязательные исправления предыдущего блока

Перед началом реализации задач, уведомлений и аудита исправить дефекты commit `cf2b7f0e` (`Block 6: HK edit form UX rework`).

### 0.1. Исправить CSS-класс PDF-строки

В `HKEdit.razor` используется:

```razor
<div class="hki-file-row">
```

Но в `HKEdit.razor.css` после последнего изменения объявлен другой класс:

```css
.hk-file-row {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 8px;
}
```

Из-за несовпадения `hki-file-row` / `hk-file-row` стиль не применяется. Исправить селектор на существующий класс разметки:

```css
.hki-file-row {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 8px;
}
```

Не переименовывать Razor-разметку, если этот класс уже применяется в других состояниях PDF-поля.

### 0.2. Выровнять поля дат по высоте

В новой структуре формы `InputDate` для `EffectiveDate` и `ExpirationDate` не имеет `class="fieldInput"`. Поэтому локальная нормализация `--hk-control-height: 38px` не применяется к ним так же, как к полю «Дата поступления».

Исправить:

```razor
<InputDate @bind-Value="card.EffectiveDate" class="fieldInput" />

<InputDate @bind-Value="card.ExpirationDate"
           class="fieldInput"
           AdditionalAttributes="@(card.EffectiveDate.HasValue
               ? new Dictionary<string, object>
               {
                   ["min"] = card.EffectiveDate.Value.ToString("yyyy-MM-dd")
               }
               : null)" />
```

Поля «Дата начала действия» и «Дата окончания действия» должны иметь ту же высоту, внутренние отступы, border-radius и focus-state, что и «Дата поступления».

### 0.3. Не скрывать ошибку загрузки PDF при создании ХК

Сейчас после сохранения новой ХК выполняется загрузка `_pendingFile`, но ошибка подавляется:

```csharp
try
{
    await UploadAttachment();
}
catch
{
    // continue — navigate even if the pending file upload failed
}

Nav.NavigateTo("/реестр-хк");
```

Такой сценарий опасен: пользователь считает, что PDF прикреплён, но карточка уже закрыта и ошибка не показана.

Заменить на контролируемое поведение:

```csharp
if (IsNew && _pendingFile != null && card.Id != Guid.Empty)
{
    await UploadAttachment();

    if (!string.IsNullOrEmpty(_attachmentError))
    {
        errorMessage = "Черновик ХК сохранён, но PDF-файл не удалось прикрепить. " +
                       "Исправьте ошибку и повторите загрузку.";
        return;
    }
}

Nav.NavigateTo("/реестр-хк");
```

Требования:

- Черновик ХК создаётся первым.
- Затем PDF привязывается к уже существующему `HKCard.Id`.
- При ошибке загрузки пользователь остаётся на странице ХК.
- Ошибка выводится в существующем `hki-field-error` и/или общем `errorMessage`.
- Не использовать пустой `catch`.
- После успешной загрузки очищать `_pendingFile`, обновлять `_attachment` и освобождать blob URL preview.

### 0.4. Размер PDF для preview и загрузки

`OnAfterRenderAsync()` использует жёсткое ограничение:

```csharp
_pendingFile.OpenReadStream(maxAllowedSize: 20 * 1024 * 1024)
```

Размер не должен быть локальной константой UI-компонента. Использовать конфигурационный параметр, единый с серверной проверкой `FileStorageService`:

```csharp
public sealed class FileStorageOptions
{
    public long MaxPdfSizeBytes { get; init; }
}
```

Получать `MaxPdfSizeBytes` через DI и применять его:

- в `OpenReadStream`;
- в API/сервисе загрузки;
- в сообщении об ошибке пользователю;
- в проверке `Content-Type`, расширения и PDF-сигнатуры `%PDF-`.

### 0.5. Предпросмотр PDF

Для новой ХК preview допускается, но не должен ломать высоту первой строки реквизитов.

Требования:

- В нормальном состоянии поле PDF имеет высоту 38px.
- После выбора PDF пользователь видит компактную строку с именем файла и действием «Предпросмотр».
- `iframe` preview не выводить внутри первой grid-строки постоянно.
- Открывать предпросмотр в отдельной модальной панели либо в отдельном компактном блоке под формой.
- При закрытии preview обязательно вызывать `URL.revokeObjectURL`.
- После загрузки файла на сервер действия «Открыть» и «Скачать» используют серверный endpoint с проверкой права `HK.View`.

---

# 1. Цель блока

Реализовать единый workflow:

```text
Событие в системе
    -> AuditLog: обязательная историческая запись
    -> Notification: информационное уведомление адресатам
    -> WorkTask: обязательное действие, если требуется исполнитель и срок
```

`WorkTask`, `Notification` и `AuditLog` — разные сущности с разной ответственностью:

| Сущность | Назначение | Требует действия |
|---|---|---:|
| `AuditLog` | Неизменяемая история действий пользователя и системы | Нет |
| `Notification` | Информационное событие в колокольчике | Нет |
| `WorkTask` | Работа с исполнителем, сроком и жизненным циклом | Да |

Нельзя использовать уведомление как замену задаче или хранить историю аудита только в тексте уведомления.

---

# 2. Доменная модель

## 2.1. Перечисления

Добавить в `Chernika.Domain.Enums`.

```csharp
public enum WorkTaskStatus
{
    Open = 1,
    InProgress = 2,
    Completed = 3,
    Cancelled = 4,
    Overdue = 5
}

public enum WorkTaskPriority
{
    Low = 1,
    Normal = 2,
    High = 3,
    Critical = 4
}

public enum WorkTaskType
{
    HKReview = 1,
    HKRevision = 2,
    HKExpirationReview = 3,
    ParentHKRevision = 4,
    ReferenceProposalReview = 5,
    UserAdministration = 6
}

public enum NotificationType
{
    Information = 1,
    TaskAssigned = 2,
    TaskCompleted = 3,
    HKSubmittedForReview = 4,
    HKReturnedForRevision = 5,
    HKApproved = 6,
    HKExpiring = 7,
    HKExpired = 8,
    ReferenceProposalPending = 9,
    System = 10
}

public enum NotificationChannel
{
    InApp = 1
}
```

`Overdue` не является произвольным пользовательским действием. Он рассчитывается сервисом либо фоновым обработчиком, когда `DueDateUtc < now` и задача не закрыта.

## 2.2. WorkTask

Добавить сущность в `Chernika.Domain.Models`.

```csharp
public class WorkTask
{
    public Guid Id { get; set; }

    public string Title { get; set; } = null!;
    public string? Description { get; set; }

    public WorkTaskType Type { get; set; }
    public WorkTaskStatus Status { get; set; }
    public WorkTaskPriority Priority { get; set; }

    public string CreatedByUserId { get; set; } = null!;
    public string? AssignedToUserId { get; set; }
    public string? AssignedRole { get; set; }

    public Guid? BranchId { get; set; }

    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public string? EntityCodeSnapshot { get; set; }
    public string? EntityTitleSnapshot { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? DueDateUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? CompletedByUserId { get; set; }
    public string? CompletionComment { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
}
```

Правила:

- Все временные поля — UTC.
- Все ID предметных сущностей — `Guid`.
- Идентификаторы пользователей — `string` из ASP.NET Identity.
- Задача имеет либо `AssignedToUserId`, либо `AssignedRole`, либо оба значения.
- `BranchId` обязателен для филиальных данных, кроме системных задач `SystemAdmin`.
- `EntityType` + `EntityId` хранит ссылку на ХК, состав, предложение справочника или другую исходную сущность.
- `EntityCodeSnapshot` и `EntityTitleSnapshot` обязательны для задач, связанных с нормативными документами: пользователь должен видеть объект даже после архивирования, переименования или soft delete.
- Физическое удаление запрещено; используется `IsDeleted = true` только для ошибочно созданных задач до начала работы. Завершённые задачи не удаляются.

## 2.3. Notification

```csharp
public class Notification
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = null!;
    public Guid? BranchId { get; set; }

    public NotificationType Type { get; set; }
    public NotificationChannel Channel { get; set; } = NotificationChannel.InApp;

    public string Title { get; set; } = null!;
    public string? Message { get; set; }

    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public Guid? WorkTaskId { get; set; }
    public string? NavigationUrl { get; set; }

    public bool IsRead { get; set; }
    public DateTime? ReadAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}
```

Правила:

- Уведомление всегда адресно: один `Notification` на одного `UserId`.
- Не хранить список адресатов JSON-строкой.
- `NavigationUrl` формируется сервисом из безопасного whitelist-маршрутов; не принимать URL из UI без проверки.
- Уведомления не заменяют аудит и не являются единственным доказательством изменения статуса.
- Удаление уведомлений не требуется в первом этапе: пользователь только помечает их прочитанными.
- Для колокольчика считаются `!IsRead` и `ExpiresAtUtc == null || ExpiresAtUtc > now`.

## 2.4. Аудит

Использовать существующую `AuditLog` и `AuditService`, не создавать второй параллельный журнал.

Требуемые поля аудита:

```text
Id
EntityType
EntityId
EntityCodeSnapshot
EntityDisplayName
Action
UserId
ActorFullName
ActorLogin
BranchId
Details
OldValues JSON
NewValues JSON
CreatedAt UTC
Source
```

Новые действия для каталога аудита:

```text
Task.Created
Task.Assigned
Task.Started
Task.Completed
Task.Cancelled
Task.Overdue
Task.Deleted

Notification.Created
Notification.Read
Notification.ReadAll

HK.ExpirationWarningCreated
HK.ExpiredArchived
HK.ParentRevisionRequired
```

Не записывать в аудит:

- пароли;
- access token;
- cookie;
- содержимое PDF;
- `StorageKey`;
- бинарные данные файла;
- персональные данные, не требуемые для отображения операции.

---

# 3. Миграции и ограничения PostgreSQL

Добавить EF Core migration.

## 3.1. Таблица WorkTasks

Индексы:

```text
IX_WorkTasks_AssignedToUserId_Status_IsDeleted
IX_WorkTasks_AssignedRole_Status_IsDeleted
IX_WorkTasks_BranchId_Status
IX_WorkTasks_EntityType_EntityId
IX_WorkTasks_DueDateUtc_Status
IX_WorkTasks_CreatedAtUtc
```

Ограничения:

```sql
CHECK (
    AssignedToUserId IS NOT NULL
    OR AssignedRole IS NOT NULL
);

CHECK (
    CompletedAtUtc IS NULL
    OR Status IN ('Completed', 'Cancelled')
);
```

При использовании integer enum значения в PostgreSQL применять числовые значения enum в `CHECK`, а не строковые литералы.

## 3.2. Таблица Notifications

Индексы:

```text
IX_Notifications_UserId_IsRead_CreatedAtUtc
IX_Notifications_UserId_ExpiresAtUtc
IX_Notifications_WorkTaskId
IX_Notifications_EntityType_EntityId
```

Защита от дублей предупреждений срока ХК:

```text
UNIQUE (UserId, Type, EntityType, EntityId, CreatedAtDateBucket)
```

Не добавлять `CreatedAtDateBucket` как вычисляемое поле без согласования. Допустимые варианты:

1. отдельное поле `DeduplicationKey`;
2. уникальный индекс по `DeduplicationKey`;
3. транзакционная проверка в `NotificationService`.

Рекомендуемый вариант:

```csharp
public string? DeduplicationKey { get; set; }
```

Примеры ключей:

```text
hk-expiry:HKCard:{HKCardId}:90d:{yyyyMMdd}
hk-expiry:HKCard:{HKCardId}:30d:{yyyyMMdd}
hk-expiry:HKCard:{HKCardId}:7d:{yyyyMMdd}
hk-expiry:HKCard:{HKCardId}:expired:{yyyyMMdd}
task-assigned:{WorkTaskId}:{UserId}
```

Добавить уникальный filtered index по непустому `DeduplicationKey`.

---

# 4. Сервисы и разделение слоёв

## 4.1. TaskService

Расположение: `Chernika.Infrastructure.Services`.

Методы:

```csharp
Task<WorkTaskDto> CreateAsync(CreateWorkTaskCommand command, CancellationToken ct);
Task<WorkTaskDto> AssignAsync(AssignWorkTaskCommand command, CancellationToken ct);
Task<WorkTaskDto> StartAsync(Guid taskId, CancellationToken ct);
Task<WorkTaskDto> CompleteAsync(CompleteWorkTaskCommand command, CancellationToken ct);
Task<WorkTaskDto> CancelAsync(CancelWorkTaskCommand command, CancellationToken ct);

Task<PagedResult<WorkTaskListItemDto>> GetMyTasksAsync(
    WorkTaskQuery query,
    CancellationToken ct);

Task<int> GetOpenTaskCountAsync(CancellationToken ct);
Task ProcessOverdueTasksAsync(CancellationToken ct);
```

Обязательные проверки в сервисе:

- право `Task.View` для чтения;
- право `Task.Assign` для назначения;
- право `Task.Complete` для завершения;
- исполнитель может завершить только назначенную ему задачу;
- `HeadOfDepartment` работает только с задачами своего `BranchId`;
- `SystemAdmin` имеет полный доступ;
- нельзя изменить закрытую или отменённую задачу;
- нельзя завершить задачу без `CompletionComment`, если тип `HKRevision` или `ReferenceProposalReview`;
- все изменения пишутся в `AuditService`;
- изменение статуса задачи создаёт уведомление адресату, когда это нужно.

## 4.2. NotificationService

```csharp
Task CreateAsync(CreateNotificationCommand command, CancellationToken ct);
Task<PagedResult<NotificationDto>> GetMyNotificationsAsync(
    NotificationQuery query,
    CancellationToken ct);

Task<int> GetUnreadCountAsync(CancellationToken ct);
Task MarkAsReadAsync(Guid notificationId, CancellationToken ct);
Task MarkAllAsReadAsync(CancellationToken ct);
Task CreateForUsersAsync(
    IEnumerable<string> userIds,
    CreateNotificationCommand command,
    CancellationToken ct);
```

Требования:

- Получать текущего пользователя через текущий пользовательский контекст, а не из параметров UI.
- При чтении/пометке «прочитано» пользователь может менять только собственные уведомления.
- `CreateForUsersAsync` создаёт записи пакетно, без N+1 `SaveChangesAsync`.
- Для фоновых уведомлений использовать `DeduplicationKey`.
- После создания/прочтения уведомления обновить счётчик topbar через Blazor state container или повторный запрос, но не хранить счётчик только в JavaScript.

## 4.3. AuditService

Все операции `TaskService`, `NotificationService`, `HKCardService` и фоновые jobs должны использовать существующий `AuditService`.

Транзакционное правило:

```text
Изменение сущности + AuditLog + связанные Notification/WorkTask
выполняются в одной транзакции AppDbContext.
```

Нельзя:

```text
SaveChangesAsync() для ХК
-> отдельный SaveChangesAsync() для аудита
-> отдельный SaveChangesAsync() для уведомления
```

Если аудит или создание обязательной задачи не выполнились, транзакция изменения статуса ХК должна откатываться.

---

# 5. Автоматические сценарии

## 5.1. ХК отправлена на проверку

Триггер: `HKCardStatus.Draft` или `RevisionRequired` → `OnReview`.

Действия:

1. `HKCardService` меняет статус.
2. Создаётся `AuditLog` с `HK.StatusChanged`.
3. Создаётся `WorkTask`:
   - `Type = HKReview`;
   - `Priority = Normal`;
   - ссылка на `HKCard`;
   - заголовок: `Проверить ХК {Code}`;
   - дедлайн: конфигурационный срок проверки;
   - назначение пользователю с правом `HK.Review` в том же филиале либо ролью `NormAdmin`.
4. Создаются `Notification` назначенным исполнителям:
   - `Type = TaskAssigned`;
   - ссылка на `WorkTaskId` и `HKCardId`;
   - переход на `/хк/{HKCardId}` или `/задачи/{TaskId}`.
5. Не создавать дубликат открытой задачи `HKReview` для той же ХК.

## 5.2. ХК возвращена на доработку

Триггер: `OnReview` → `RevisionRequired`.

Действия:

1. Создать аудит изменения статуса.
2. Закрыть/отменить активную задачу `HKReview` для этой ХК с системной причиной.
3. Создать задачу `HKRevision` автору ХК.
4. `CompletionComment` или причина возврата обязательна.
5. Создать уведомление автору:
   - `ХК {Code} возвращена на доработку`;
   - ссылка на карточку;
   - текст замечания без HTML.

## 5.3. ХК утверждена

Триггер: `OnReview` → `Approved`.

Действия:

1. Создать аудит.
2. Завершить задачу `HKReview`.
3. Создать уведомление автору ХК об утверждении.
4. Если утверждена дочерняя ХК, найти утверждённые родительские ХК, содержащие её через `HKCardComponent`.
5. Для каждой действующей родительской ХК создать задачу `ParentHKRevision` только при реальной необходимости пересмотра, определённой бизнес-правилом.
6. Не изменять утверждённую родительскую ХК автоматически и не подменять `ChildHKCardId`.

## 5.4. Истечение срока действия ХК

Создать фоновую задачу, например:

```csharp
public sealed class HKExpirationBackgroundService : BackgroundService
```

Либо использовать утверждённый планировщик, если он уже есть в проекте. Не запускать отдельный бесконечный цикл внутри Razor-компонента.

Ежедневный запуск:

1. Найти `Approved` ХК с `ExpirationDate`.
2. Учитывать пороги из конфигурации: `90`, `30`, `7`, `0` дней.
3. За 90 дней создать информационное уведомление ответственному/автору/контролёру по утверждённому правилу.
4. За 30 и 7 дней создать:
   - уведомление;
   - одну задачу `HKExpirationReview` ответственному.
5. В день истечения создать уведомление `HKExpired`.
6. После истечения перевести ХК `Approved` → `Archived` через `HKCardService`, а не прямым SQL-изменением статуса.
7. Создать системную запись аудита с `Source = System`.
8. Не создавать дубли благодаря `DeduplicationKey`.
9. Значения порогов, время запуска и срок задачи хранить в конфигурации.

Пример:

```json
"HKExpiration": {
  "WarningDays": [90, 30, 7],
  "DailyRunTimeUtc": "01:00",
  "ReviewTaskDueDays": 14
}
```

## 5.5. Предложения справочников

Триггер: создан `ReferenceProposal`.

Действия:

- Создать задачу `ReferenceProposalReview` пользователю/роли с правом `Reference.Edit`.
- Создать уведомление о новом предложении.
- При `Accepted` или `Rejected` завершить задачу и уведомить инициатора.
- Не создавать реальный `Node`, `AssemblyUnit` или `GsmMaterial` до принятия предложения.

---

# 6. UI: раздел «Задачи»

Добавить страницу:

```text
/задачи
```

Компонент: `Chernika.Web/Pages/Tasks.razor`.

## 6.1. Реестр

Использовать стандартные классы UI-kit:

```text
.panel
.panelHeader
.field
.btn
.btn.primary
.tag
.status
table
.searchTop
```

В верхней панели:

```text
Мои задачи
Открытые: N
Просроченные: N
Выполненные за 30 дней: N
```

Фильтры:

- текст;
- статус;
- тип задачи;
- приоритет;
- срок: все / сегодня / неделя / просроченные;
- связанная сущность;
- филиал — только при наличии разрешения межфилиального просмотра;
- сортировка: срок, приоритет, дата создания, статус.

Колонки таблицы:

| Колонка | Содержимое |
|---|---|
| Приоритет | tag / цвет по `--ok`, `--warn`, `--bad` |
| Задача | заголовок и краткое описание |
| Объект | `EntityCodeSnapshot`, `EntityTitleSnapshot`, ссылка |
| Исполнитель | ФИО/логин |
| Срок | дата и статус просрочки |
| Статус | `Open`, `InProgress`, `Completed`, `Cancelled`, `Overdue` |
| Действия | Открыть, Взять в работу, Завершить |

Требования UI:

- `Guest` не видит кнопок изменения.
- Read-only режим не должен показывать форму завершения.
- При ширине <560px таблица остаётся в горизонтально прокручиваемом wrapper.
- Сортировка, фильтрация и пагинация — серверные.
- Нельзя загружать все задачи в Blazor circuit и фильтровать в памяти.

## 6.2. Карточка задачи

Добавить просмотр задачи в модальном окне или отдельном маршруте:

```text
/задачи/{id}
```

Показывать:

- заголовок;
- описание;
- тип;
- приоритет;
- статус;
- инициатор;
- исполнитель;
- дата создания;
- срок;
- связанная ХК/предложение;
- история действий из аудита;
- поле комментария выполнения;
- кнопки допустимых переходов.

Не разрешать редактировать тип, исходную сущность и дату создания после создания задачи.

## 6.3. Бейдж sidebar

У пункта навигации «Задачи» вывести бейдж точного количества задач:

```text
Status IN (Open, InProgress, Overdue)
AND AssignedToUserId == currentUserId
AND IsDeleted == false
```

Если задача назначена роли, учитывать её только после правила разрешённого отображения роли текущему пользователю. Не суммировать все задачи `NormAdmin` каждому пользователю без принятого правила распределения.

Обновление бейджа обязательно после:

- создания задачи;
- назначения;
- взятия в работу;
- завершения;
- отмены;
- фоновой постановки задач.

---

# 7. UI: колокольчик уведомлений

Добавить/доработать компонент topbar.

## 7.1. Счётчик

Колокольчик показывает число непрочитанных уведомлений текущего пользователя.

Правила:

- `0` — бейдж скрыт;
- `1–99` — точное число;
- `100+` — `99+`;
- данные получаются через `NotificationService`;
- не доверять счётчику из localStorage или JS.

## 7.2. Выпадающий список

Показывать последние 20 уведомлений:

- иконка типа;
- заголовок;
- короткое сообщение;
- дата/время;
- признак прочитанного;
- переход к задаче или ХК;
- действие «Отметить прочитанным»;
- действие «Отметить все прочитанными»;
- ссылка «Все уведомления».

При клике по уведомлению:

1. пометить его прочитанным;
2. выполнить безопасный переход;
3. обновить счётчик.

При ошибке перехода уведомление не удалять и не считать прочитанным до подтверждённого действия.

## 7.3. Страница уведомлений

Добавить:

```text
/уведомления
```

Фильтры:

- все / непрочитанные;
- тип;
- период;
- связанная сущность.

Пагинация и сортировка — серверные.

---

# 8. UI: аудит

Использовать существующую страницу аудита и существующий `AuditService`, расширив её фильтры и каталог действий.

Добавить фильтры:

- пользователь;
- период дат;
- сущность;
- код/наименование объекта;
- действие;
- источник: пользователь / система / фоновая задача;
- филиал;
- поиск по `Details`.

Новые группы действий:

```text
Задачи
Уведомления
Сроки ХК
Статусы ХК
Справочники
Пользователи и разрешения
```

В деталях записи показывать:

- русское отображаемое название действия;
- объект по snapshot;
- автора действия;
- источник;
- старые/новые значения в безопасном читаемом формате;
- ссылку на сущность, если она существует и у пользователя есть право просмотра.

Не выводить сырой JSON по умолчанию. Добавить отдельный режим «Технические данные» только для `SystemAdmin`.

---

# 9. Права доступа

Добавить/проверить permissions:

```text
Task.View
Task.Assign
Task.Complete
Task.Cancel
Notification.View
Notification.MarkRead
Audit.View
```

Минимальные правила:

| Роль | Задачи | Уведомления | Аудит |
|---|---|---|---|
| SystemAdmin | Полный доступ | Свои | Полный доступ |
| NormAdmin | Свои и назначенные в филиале | Свои | В пределах филиала |
| Operator | Только свои | Свои | Нет, если не дано явно |
| HeadOfDepartment | Задачи подразделения | Свои | В пределах подразделения |
| Guest | Нет выполнения и назначения | Только свои информационные | Нет, если не дано явно |

Проверки выполняются:

1. policy-based authorization в Presentation Layer;
2. повторно в `TaskService`, `NotificationService`, `AuditService`;
3. с обязательным ограничением `BranchId` на уровне запросов.

Скрытие кнопки в Razor не является проверкой безопасности.

---

# 10. Тесты

## Unit tests

Покрыть:

- создание задачи;
- назначение на пользователя;
- запрет завершения чужой задачи;
- переходы `Open → InProgress → Completed`;
- запрет изменения `Completed`/`Cancelled`;
- расчёт `Overdue`;
- создание уведомлений без дублей;
- `DeduplicationKey`;
- фильтрацию по филиалу;
- создание аудита при каждой операции;
- отсутствие аудита/уведомления при откате транзакции;
- переход ХК `Draft → OnReview`;
- возврат `OnReview → RevisionRequired`;
- утверждение `OnReview → Approved`;
- сроковые уведомления 90/30/7/0 дней;
- архивирование истёкшей Approved ХК;
- отсутствие дублей при повторном запуске фоновой задачи.

## Integration tests

Проверить PostgreSQL:

- FK;
- unique index `DeduplicationKey`;
- индексы фильтрации;
- transactional rollback;
- server-side pagination;
- ограничения филиала;
- soft delete задач.

## Ручная приёмка

1. Оператор отправляет ХК на проверку.
2. NormAdmin получает задачу и уведомление.
3. У sidebar «Задачи» появляется корректный бейдж.
4. NormAdmin возвращает ХК на доработку.
5. Автор получает задачу и уведомление.
6. Автор дорабатывает и повторно отправляет ХК.
7. NormAdmin утверждает ХК.
8. Закрытая задача проверки исчезает из открытых.
9. При сроке ХК 30 дней создаётся ровно одно уведомление и ровно одна задача.
10. Повторный запуск фоновой задачи не создаёт дубликаты.
11. Все действия видны в аудите.
12. Пользователь другого филиала не видит чужие задачи, уведомления и аудит.
13. В светлой/тёмной теме и на breakpoint 1120px/560px UI не ломается.

---

# Definition of Done

- Исправлены дефекты предыдущего блока: CSS PDF-строки, высота `InputDate`, обработка ошибки отложенной загрузки PDF и конфигурация лимита файла.
- `WorkTask`, `Notification` и `AuditLog` реализованы как отдельные сущности и сценарии.
- Каждое критичное изменение ХК сопровождается транзакционной записью аудита.
- Отправка ХК на проверку, возврат на доработку, утверждение и истечение срока создают нужные задачи и уведомления без дублей.
- Есть серверные реестры задач, уведомлений и аудита.
- Sidebar badge и колокольчик используют серверные счётчики текущего пользователя.
- Все права и филиальные ограничения проверяются на уровне сервисов.
- Фоновые задания не дублируют уведомления/задачи и журналируют системные действия.
- Реализация соответствует 4-слойной архитектуре, EF Core/Npgsql, Blazor UI-kit и UTC/Guid-ограничениям проекта.

---
## 13. Разбиение на PR

PR 0 — Hotfix формы ХК

Отдельный маленький PR, не включать в блок задач:

    Исправить .hk-file-row → .hki-file-row.

    Добавить class="fieldInput" полям EffectiveDate и ExpirationDate.

    Не подавлять ошибку отложенной загрузки PDF после создания Draft.

    Вынести лимит PDF из HKEdit.razor в конфигурацию FileStorageOptions.

    Переместить preview PDF из первой строки реквизитов в отдельную панель/модальное окно.

Критерий: форма ХК стабильно работает до изменения сервисов статусов, задач и уведомлений.
PR 5.1 — Основа: модель, миграция, сервисы

Цель: добавить серверную основу без UI и без автоматических сценариев ХК.
Включить

    Domain:

        WorkTask;

        Notification;

        WorkTaskStatus, WorkTaskPriority, WorkTaskType;

        NotificationType, NotificationChannel.

    EF Core:

        конфигурации сущностей;

        миграцию WorkTasks и Notifications;

        индексы;

        check constraints;

        уникальный filtered index для Notification.DeduplicationKey.

    Application Services:

        каркас TaskService;

        каркас NotificationService;

        DTO и command/query-модели;

        серверная фильтрация и пагинация.

    Permissions:

        Task.View, Task.Assign, Task.Complete, Task.Cancel;

        Notification.View, Notification.MarkRead.

    Audit:

        расширить AuditDisplayCatalog действиями Task.* и Notification.*;

        писать аудит для ручных операций над задачами и уведомлениями.

    Unit/integration tests:

        создание;

        назначение;

        ограничения филиала;

        проверки прав;

        защита от дублей DeduplicationKey.

Не включать

    Страницу задач.

    Колокольчик.

    Изменения статусов ХК.

    Фоновую обработку сроков.

Критерий PR: можно программно создать, получить, назначить, завершить задачу и создать/прочитать уведомление; данные защищены миграцией и сервисными проверками.
PR 5.2 — Реестр задач и workflow ХК

Цель: дать пользователям полноценную работу с задачами и связать её со статусами ХК.
Включить

    Tasks.razor и маршрут /задачи.

    Серверные фильтры:

        текст;

        статус;

        тип;

        приоритет;

        срок;

        просроченность;

        сортировка;

        пагинация.

    Карточка задачи: отдельный маршрут /задачи/{id} либо модальное окно.

    Действия:

        «Взять в работу»;

        «Завершить»;

        «Отменить» — только по permission;

        комментарий выполнения.

    Бейдж открытых задач в sidebar.

    Интеграция с HKCardService:

        Draft/RevisionRequired → OnReview создаёт HKReview;

        OnReview → RevisionRequired отменяет/закрывает HKReview и создаёт HKRevision;

        OnReview → Approved завершает HKReview.

    Аудит всех переходов задачи и статусов ХК.

Не включать

    Колокольчик и страницу уведомлений.

    Фоновую обработку сроков.

    Автоматические задачи родительских ХК.

Критерий PR: отправка ХК на проверку и возврат на доработку создают корректные задачи; исполнитель видит их в своём реестре и sidebar badge обновляется.
PR 5.3 — Уведомления и колокольчик

Цель: реализовать пользовательские информационные уведомления поверх уже готовых задач и workflow.
Включить

    Компонент topbar:

        колокольчик;

        число непрочитанных: 1–99, 99+;

        dropdown последних 20 уведомлений.

    Страница /уведомления.

    Действия:

        открыть уведомление;

        пометить прочитанным;

        «Прочитать все».

    Безопасная навигация:

        на ХК;

        на задачу;

        только через whitelist маршрутов.

    Интеграции:

        назначение задачи → TaskAssigned;

        возврат ХК → HKReturnedForRevision;

        утверждение ХК → HKApproved;

        создание предложения справочника → ReferenceProposalPending.

    Обновление счётчика после чтения и изменения задач.

    Аудит Notification.Read и Notification.ReadAll.

Не включать

    Daily job истечения срока.

    Автоархивирование ХК.

    Массовые повторные уведомления.

Критерий PR: назначенный исполнитель получает уведомление, открывает его, попадает в нужную задачу/ХК, а счётчик непрочитанных корректно обновляется.
PR 5.4 — Сроки ХК, фоновые работы и расширенный аудит

Цель: автоматизировать контроль срока действия ХК и завершить audit-workflow.
Включить

    Конфигурацию HKExpirationOptions:

        пороги 90/30/7/0 дней;

        время ежедневного запуска;

        срок задачи пересмотра.

    Фоновый обработчик:

        поиск Approved ХК с ExpirationDate;

        уведомления за 90/30/7/0 дней;

        задача HKExpirationReview за 30 и 7 дней;

        автоматический Approved → Archived после истечения;

        отсутствие дублей через DeduplicationKey.

    Системный аудит:

        HK.ExpirationWarningCreated;

        HK.ExpiredArchived;

        Task.Overdue.

    Расширение UI аудита:

        источник: пользователь / система / background job;

        фильтры задач и уведомлений;

        русские названия новых действий;

        отображение snapshots объекта.

    Полный integration-test background workflow.

Не включать

    Новые каналы: email, Telegram, SMS.

    Пользовательские настройки сроков уведомлений.

    Переназначение массовых задач.

Критерий PR: повторный запуск фоновой обработки не создаёт дубли; истёкшая утверждённая ХК архивируется через HKCardService, а все системные действия видны в аудите.
Зависимости
PR	Зависит от	Результат
PR 0	—	    Стабильная форма ХК
PR 5.1	PR 0	Сущности, миграции, сервисы, permissions
PR 5.2	PR 5.1	Реестр задач и lifecycle ХК
PR 5.3	PR 5.1, PR 5.2	Колокольчик и уведомления workflow
PR 5.4	PR 5.1, PR 5.3	Сроковые jobs, автоархив, полный аудит

Технически блок из проектного описания объединяет задачи, уведомления и аудит, но их разумно вводить последовательно: сначала хранение и права, затем обязательные действия, потом информационные сообщения и в конце фоновый lifecycle сроков ХК

Не объединять PR . Каждый PR должен проходить migration review, unit/integration tests и ручную проверку ролями



# 14. Ограничение объёма автоматических тестов

## Цель

На текущем этапе не создавать полный контур unit- и integration-тестов для блока задач, уведомлений и аудита. Приоритет — корректная миграция, рабочий workflow и ручная smoke-проверка основных сценариев без существенного расхода времени и токенов на тестовую инфраструктуру.

Не создавать в этом PR:

- отдельные проекты `Chernika.UnitTests` и `Chernika.IntegrationTests`, если их ещё нет;
- Testcontainers;
- тестовую PostgreSQL-инфраструктуру;
- фикстуры с реальными connection string;
- тесты всех комбинаций ролей, фильтров, сортировок и пагинации;
- тесты каждого текста уведомления;
- массовые тесты фоновых задач;
- сложные моки Blazor UI;
- нагрузочные тесты.

Если такие проекты или тесты уже существуют в репозитории, не удалять их и не переписывать без отдельной задачи. Новые объёмные тесты в рамках данного блока не добавлять.

## Обязательный минимальный smoke-check

Выполнить только следующие проверки перед завершением PR.

1. Проект собирается без ошибок:

```bash
dotnet build
```

2. EF Core migration применяется к чистой тестовой или локальной БД:

```bash
dotnet ef database update
```

3. Вручную проверить создание задачи:

```text
SystemAdmin или NormAdmin создаёт задачу,
задача сохраняется,
исполнитель видит её в реестре.
```

4. Вручную проверить lifecycle задачи:

```text
Open → InProgress → Completed
```

Проверить, что завершённую задачу нельзя повторно завершить или изменить.

5. Вручную проверить ограничения доступа:

```text
Оператор не может назначить или отменить задачу.
Guest не может завершить задачу.
Пользователь не может открыть или отметить прочитанным уведомление другого пользователя.
```

6. Вручную проверить уведомление:

```text
При назначении задачи конкретному исполнителю создаётся одно уведомление.
Повторное выполнение того же сценария не создаёт дубль при одинаковом DeduplicationKey.
```

7. Вручную проверить аудит:

```text
Создание, назначение, старт, завершение и отмена задачи
создают записи AuditLog.
```

8. Вручную проверить миграцию legacy `WorkTasks`:

```text
Существующие задачи после миграции не теряются,
имеют корректный Status,
CreatedAtUtc,
DueDateUtc,
AssignedToUserId.
```

## Правило разработки

Если реализация требует большого количества тестового кода, mock-объектов, Docker/Testcontainers или отдельной тестовой БД, не расширять задачу самостоятельно.

Вместо этого:

1. Реализовать рабочую серверную логику.
2. Выполнить перечисленный smoke-check.
3. Зафиксировать в PR, какие сценарии проверены вручную.
4. Вынести полный автоматический тестовый контур отдельным backlog-задачей после стабилизации workflow.

## Definition of Done для тестирования

- `dotnet build` проходит.
- Миграция применяется без ошибки.
- Основные сценарии задач, уведомлений, прав и аудита вручную проверены.
- Не добавлена сложная тестовая инфраструктура.
- Не добавлены connection string, пароли или секреты в репозиторий.
- Полный набор unit/integration-тестов отложен в отдельную задачу.