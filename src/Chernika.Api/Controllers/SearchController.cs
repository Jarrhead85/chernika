using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chernika.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class SearchController : ControllerBase
{
    private readonly SearchService _search;

    public SearchController(SearchService search) => _search = search;

    [HttpGet]
    public async Task<ActionResult<SearchPageDto>> Search([FromQuery] SearchQuery query, CancellationToken ct)
    {
        try
        {
            var result = await _search.SearchAsync(query, ct);
            return Ok(result);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { error = "Некорректные параметры поиска." });
        }
    }
}
