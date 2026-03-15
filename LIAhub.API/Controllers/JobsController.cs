using LIAhub.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LIAhub.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class JobsController : ControllerBase
{
    private readonly AppDbContext _db;

    public JobsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetJobs(
        [FromQuery] string? city,
        [FromQuery] string? tech,
        [FromQuery] string? search,
        [FromQuery] string? skills)
    {
        var query = _db.CachedJobs.AsQueryable();

        if (!string.IsNullOrEmpty(city))
            query = query.Where(j => j.City != null &&
                j.City.ToLower().Contains(city.ToLower()));

        if (!string.IsNullOrEmpty(tech))
            {
                var techs = tech.Split(',').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();
                query = query.Where(j => techs.Any(t => j.TechTags.Contains(t)));
            }

        if (!string.IsNullOrEmpty(search))
            query = query.Where(j =>
                j.Title.ToLower().Contains(search.ToLower()) ||
                j.Employer.ToLower().Contains(search.ToLower()));

        var jobs = await query
            .OrderByDescending(j => j.RelevanceScore)
            .ToListAsync();

        // Parse user skills from query param
        var userSkills = string.IsNullOrEmpty(skills)
            ? new List<string>()
            : skills.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();

        var result = jobs.Select(j =>
        {
            var matchedSkills = userSkills.Any()
                ? j.TechTags.Intersect(userSkills, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();

            var missingSkills = userSkills.Any()
                ? userSkills.Except(j.TechTags, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();

            var matchScore = userSkills.Any()
                ? Math.Round((double)matchedSkills.Count / userSkills.Count, 2)
                : 0.0;

            var locationMatched = !string.IsNullOrEmpty(city) && !string.IsNullOrEmpty(j.City) &&
                j.City.Contains(city, StringComparison.OrdinalIgnoreCase);

            return new
            {
                j.Id,
                j.ExternalId,
                j.Title,
                j.Employer,
                j.City,
                j.WorkMode, 
                j.TechTags,
                j.StudentSignals,
                j.NegativeSignals,
                j.RelevanceScore,
                j.Url,
                j.FetchedAt,
                j.PublishedAt,
                MatchScore = matchScore,
                MatchedSkills = matchedSkills,
                MissingSkills = missingSkills,
                LocationMatched = locationMatched
            };
        })
        .OrderByDescending(j => j.MatchScore)
        .ThenByDescending(j => j.RelevanceScore)
        .ToList();

        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetJob(Guid id)
    {
        var job = await _db.CachedJobs.FindAsync(id);
        if (job == null) return NotFound();
        return Ok(job);
    }
}