using System.Text.Json;
using System.Text.RegularExpressions;
using LIAhub.Core.Models;

namespace LIAhub.Infrastructure.Services;

public class JobSearchService
{
    private readonly HttpClient _httpClient;
    private const string BaseUrl = "https://jobsearch.api.jobtechdev.se";

    public JobSearchService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<CachedJob>> FetchLiaJobsAsync()
    {
        var allJobs = new List<CachedJob>();

        // Search with multiple keyword combinations to maximize results
        var searchQueries = new[]
        {
            "systemutvecklare praktik",
            "webbutvecklare praktik",
            "utvecklare junior",
            "developer internship",
            "mjukvaruutvecklare student",
            ".NET junior",
            "React junior",
            "backend junior",
            "frontend junior",
            "LIA utvecklare"
        };

        foreach (var keyword in searchQueries)
        {
            var offset = 0;
            var limit = 100;
            var hasMore = true;

            while (hasMore)
            {
                var url = $"{BaseUrl}/search?q={Uri.EscapeDataString(keyword)}&limit={limit}&offset={offset}";

                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<JobSearchResponse>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                // Stop if no results returned
                if (result?.Hits == null || result.Hits.Count == 0)
                {
                    hasMore = false;
                    break;
                }

                var filteredJobs = result.Hits
                    // Avoid duplicates from overlapping searches
                    .Where(hit => allJobs.All(j => j.ExternalId != hit.Id))
                    .Select(hit =>
                    {
                        var techTags = ExtractTechTags(hit.Description?.Text);
                        var studentSignals = ExtractStudentSignals(hit.Headline, hit.Description?.Text);
                        var negativeSignals = ExtractNegativeSignals(hit.Headline, hit.Description?.Text);
                        var relevanceScore = CalculateRelevanceScore(techTags, studentSignals, negativeSignals);
                        var workMode = ExtractWorkMode(hit.Headline, hit.Description?.Text);

                        return new CachedJob
                        {
                            Id = Guid.NewGuid(),
                            ExternalId = hit.Id,
                            Title = hit.Headline ?? string.Empty,
                            Employer = hit.Employer?.Name ?? string.Empty,
                            City = hit.WorkplaceAddress?.City != null 
                                ? System.Globalization.CultureInfo.CurrentCulture.TextInfo
                                    .ToTitleCase(hit.WorkplaceAddress.City.ToLower())
                                : hit.WorkplaceAddress?.Municipality != null
                                ? System.Globalization.CultureInfo.CurrentCulture.TextInfo
                                    .ToTitleCase(hit.WorkplaceAddress.Municipality.ToLower())
                                : null,
                            Description = hit.Description?.Text,
                            TechTags = techTags,
                            StudentSignals = studentSignals,
                            NegativeSignals = negativeSignals,
                            RelevanceScore = relevanceScore,
                            Url = hit.WebPage ?? $"https://arbetsformedlingen.se/platsbanken/annonser/{hit.Id}",
                            FetchedAt = DateTime.UtcNow,
                            WorkMode = workMode,
                            // Cache the listing for 6 hours, then fetch fresh data
                            ExpiresAt = DateTime.UtcNow.AddHours(12)
,
                            PublishedAt = hit.PublicationDate.HasValue 
                                ? DateTime.SpecifyKind(hit.PublicationDate.Value, DateTimeKind.Utc)
                                : null,
                        };
                    })
                    // Only keep jobs with a minimum relevance score
                    .Where(job => job.RelevanceScore >= 20)
                    .Where(job => !job.Title.Contains("senior", StringComparison.OrdinalIgnoreCase))
                    .Where(job => !job.Title.Contains("lead", StringComparison.OrdinalIgnoreCase))
                    .Where(job => !job.Title.Contains("architect", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                allJobs.AddRange(filteredJobs);

                // If we got fewer than limit, there are no more pages
                if (result.Hits.Count < limit)
                    hasMore = false;
                else
                    offset += limit;
            }
        }

        return allJobs;
    }

    private List<string> ExtractTechTags(string? description)
    {
        if (string.IsNullOrEmpty(description)) return new List<string>();

        var tags = new List<string>();

        // Full list of technologies — users will later filter listings by these via their profile
        var techKeywords = new[]
        {
            // .NET ecosystem
            ".NET", "C#", "ASP.NET", "Blazor", "Entity Framework", "MAUI",
            // Frontend
            "React", "Angular", "Vue", "TypeScript", "JavaScript", "HTML", "CSS",
            // Backend
            "Java", "Spring", "Python", "Node.js", "PHP", "Go", "Rust", "Kotlin",
            // Database
            "SQL", "PostgreSQL", "MySQL", "MongoDB", "Redis", "SQLite",
            // Cloud & DevOps
            "Azure", "AWS", "Docker", "Kubernetes", "CI/CD", "GitHub Actions",
            // Other
            "REST", "API", "Git", "Linux", "Scrum", "Agile"
        };

        foreach (var keyword in techKeywords)
        {
            if (description.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                tags.Add(keyword);
        }

        return tags;
    }

    private List<string> ExtractStudentSignals(string? title, string? description)
    {
        var text = $"{title} {description}".ToLower();
        var signals = new List<string>();

        // Positive signals indicating student-friendly positions
        var studentKeywords = new[]
        {
            "praktik", "praktikant", "lia", "lärande i arbete",
            "internship", "intern", "trainee",
            "student", "graduate", "examensarbete", "thesis", "entry level",
            "nyexaminerad", "nyutexaminerad", "fresh graduate"
        };

        foreach (var keyword in studentKeywords)
        {
            if (keyword == "lia")
            {
                if (Regex.IsMatch(text, @"\blia\b"))
                    signals.Add(keyword);
            }
            else if (text.Contains(keyword))
            {
                signals.Add(keyword);
            }
        }
                // Only extract "junior" from title, not description
        if (!string.IsNullOrEmpty(title) && title.ToLower().Contains("junior"))
            signals.Add("junior");

        return signals;
    }

    private List<string> ExtractNegativeSignals(string? title, string? description)
    {
        var text = $"{title} {description}".ToLower();
        var signals = new List<string>();

        // Negative signals indicating senior-level positions
        var negativeKeywords = new[]
        {
            "senior", "lead developer", "team lead", "architect",
            "3+ years", "5+ years", "minst 3 år", "minst 5 år",
            "several years", "flera års erfarenhet", "experienced"
        };

        foreach (var keyword in negativeKeywords)
        {
            if (text.Contains(keyword))
                signals.Add(keyword);
        }

        return signals;
    }

    private int CalculateRelevanceScore(
        List<string> techTags,
        List<string> studentSignals,
        List<string> negativeSignals)
    {
        var score = 0;

        // Tech tags found = more relevant
        score += techTags.Count * 5;

        // Student signals boost score significantly
        score += studentSignals.Count * 20;

        // Negative signals reduce score
        score -= negativeSignals.Count * 25;

        // Keep score between 0 and 100
        return Math.Clamp(score, 0, 100);
    }
    
    private string ExtractWorkMode(string? title, string? description)
    {
        var text = $"{title} {description}".ToLower();

        if (text.Contains("remote"))
            return "Remote";

        if (text.Contains("hybrid"))
            return "Hybrid";

        return "På plats";
    }
}

// Models for deserializing the response from the JobSearch API
public class JobSearchResponse
{
    public List<JobHit>? Hits { get; set; }
}

public class JobHit
{
    public string Id { get; set; } = string.Empty;
    public string? Headline { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("webpage_url")]
    public string? WebPage { get; set; }
    
    public EmployerInfo? Employer { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("workplace_address")]
    public WorkplaceAddress? WorkplaceAddress { get; set; }
    
    public DescriptionInfo? Description { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("publication_date")]
    public DateTime? PublicationDate { get; set; }  
}

public class EmployerInfo
{
    public string? Name { get; set; }
}

public class WorkplaceAddress
{
    public string? Municipality { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
}

public class DescriptionInfo
{
    public string? Text { get; set; }
}