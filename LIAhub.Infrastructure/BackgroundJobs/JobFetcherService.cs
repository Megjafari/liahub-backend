using LIAhub.Infrastructure.Data;
using LIAhub.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LIAhub.Core.Models;

namespace LIAhub.Infrastructure.BackgroundJobs;

public class JobFetcherService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobFetcherService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(6);

    public JobFetcherService(IServiceScopeFactory scopeFactory, ILogger<JobFetcherService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("JobFetcherService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await FetchAndStoreJobsAsync();
            await Task.Delay(_interval, stoppingToken);
        }
    }

            public async Task FetchAndStoreJobsAsync()
            {
                try
                {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var jobSearchService = scope.ServiceProvider.GetRequiredService<JobSearchService>();
            var notificationService = scope.ServiceProvider.GetRequiredService<NotificationService>();

            _logger.LogInformation("Fetching jobs from JobSearch API...");

            var jobs = await jobSearchService.FetchLiaJobsAsync();
            _logger.LogInformation("Jobs after filtering: {Count}", jobs.Count);

            var fetchedExternalIds = jobs.Select(j => j.ExternalId).ToHashSet();

            // Remove jobs that no longer exist on Arbetsförmedlingen
            var removed = await db.CachedJobs
                .Where(j => !fetchedExternalIds.Contains(j.ExternalId))
                .ToListAsync();
            db.CachedJobs.RemoveRange(removed);

            // Add only genuinely new jobs
            var newJobs = new List<CachedJob>();
            foreach (var job in jobs)
            {
                var exists = await db.CachedJobs
                    .AnyAsync(j => j.ExternalId == job.ExternalId);

                if (!exists)
                {
                    await db.CachedJobs.AddAsync(job);
                    newJobs.Add(job);
                }
            }

            await db.SaveChangesAsync();
            _logger.LogInformation("Fetched and stored {Count} jobs.", jobs.Count);

            if (newJobs.Any())
                await notificationService.SendNewJobNotificationsAsync(newJobs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching jobs.");
        }
    }
    }