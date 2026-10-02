using Chernika.Api.Contracts;
using Chernika.Domain;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

/// <summary>
/// REST-доступ ко второму справочнику направленных связей марок ГСМ (PR-4).
/// <para>
/// Контроллер — только транспорт: вся бизнес-логика и все проверки пары,
/// Foreign и мягкого удаления живут в <see cref="GsmMaterialService"/>. Отдельного
/// хранилища или дублирующих правил здесь нет.
/// </para>
/// <para>
/// Права те же, что у справочника марок: чтение требует справочной роли,
/// запись — права редактирования. Справочник общесистемный, фильтра по BranchId
/// нет.
/// </para>
/// </summary>
[ApiController]
[Authorize]
[Route("api/[controller]")]
public class GsmMaterialRelationsController : ControllerBase
{
    private readonly GsmMaterialService _gsmService;

    public GsmMaterialRelationsController(GsmMaterialService gsmService) => _gsmService = gsmService;

    /// <summary>Постраничный список связей с поиском и фильтрами.</summary>
    [HttpGet]
    public async Task<ActionResult<GsmRelationListApiResponse>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 15,
        [FromQuery] string? search = null,
        [FromQuery] string? relationType = null,
        [FromQuery] Guid? primaryGsmMaterialId = null,
        [FromQuery] bool showDeleted = false,
        [FromQuery] string? sortBy = null,
        [FromQuery] bool sortDescending = false)
    {
        GsmRelationType? type = null;
        if (!string.IsNullOrWhiteSpace(relationType))
            type = GsmRelationMapper.ToRelationType(relationType);

        var result = await _gsmService.GetRelationsPagedAsync(new GsmRelationQuery
        {
            Page = page,
            PageSize = pageSize,
            Search = search,
            RelationType = type,
            PrimaryGsmMaterialId = primaryGsmMaterialId,
            ShowDeleted = showDeleted,
            SortBy = sortBy,
            SortDescending = sortDescending,
        });

        return Ok(new GsmRelationListApiResponse(
            result.Items.Select(GsmRelationMapper.ToDto).ToList(),
            result.TotalCount, result.Page, result.PageSize, result.TotalPages));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<GsmRelationDto>> GetById(Guid id)
    {
        var view = await _gsmService.GetRelationEditViewAsync(id);
        if (view == null) return NotFound();
        return Ok(GsmRelationMapper.ToDto(view));
    }

    /// <summary>
    /// Марки для выбора в форме связи (опубликованные, неудалённые).
    /// <para>
    /// Поиск выполняется на сервере по <c>search</c>; 200 — предел одной выдачи,
    /// а не всего справочника. <c>includeIds</c> возвращает конкретные марки
    /// (например, выбранные в редактируемой связи), даже если они не попали в
    /// выдачу поиска. Подписи различимы: уникальности <c>Name</c> в БД нет.
    /// </para>
    /// </summary>
    [HttpGet("material-options")]
    public async Task<ActionResult<List<GsmRelationMaterialOptionDto>>> GetMaterialOptions(
        [FromQuery] string? search = null,
        [FromQuery] Guid[]? includeIds = null)
    {
        var options = await _gsmService.GetRelationMaterialOptionsAsync(search, default, includeIds);
        return Ok(options.Select(GsmRelationMapper.ToDto).ToList());
    }

    [HttpPost]
    [Authorize(Policy = "CreateEquipment")]
    public async Task<ActionResult<GsmRelationDto>> Create([FromBody] GsmRelationWriteApiRequest request)
    {
        try
        {
            var created = await _gsmService.CreateRelationAsync(GsmRelationMapper.ToWriteRequest(request));
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, GsmRelationMapper.ToDto(created));
        }
        catch (InvalidOperationException ex)
        {
            // Отказ по правилам связи (повтор активной пары, самоссылка, Foreign
            // при InGostNomenclature = true) — это НЕ серверная ошибка. Без
            // перехвата клиент получал сырой 500 с text/plain вместо понятного
            // ответа с причиной; HTTP-тест это зафиксировал.
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "EditEquipment")]
    public async Task<ActionResult> Update(Guid id, [FromBody] GsmRelationWriteApiRequest request)
    {
        try
        {
            var updated = await _gsmService.UpdateRelationAsync(
                id, GsmRelationMapper.ToWriteRequest(request));
            if (updated == null) return NotFound();
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "DeleteEquipment")]
    public async Task<ActionResult> Delete(Guid id)
    {
        try
        {
            if (!await _gsmService.DeleteRelationAsync(id)) return NotFound();
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }
}
