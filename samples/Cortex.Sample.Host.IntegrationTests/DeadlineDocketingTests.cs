using System.Net.Http.Json;
using Cortex.Infrastructure.Persistence;
using Cortex.Modules.Legal;
using Cortex.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Sample.Host.IntegrationTests;

/// <summary>
/// Docketing end to end: a deadline docketed on a matter surfaces in the Deadlines tab endpoint,
/// the reminder scanner produces exactly ONE inbox notification for the user who docketed it
/// (running cross-tenant, outside any request scope), and completed items stop reminding.
/// </summary>
[Collection("api")]
public sealed class DeadlineDocketingTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Deadline_SurfacesInTab_AndRemindsExactlyOnce()
    {
        var owner = Guid.NewGuid();
        var marker = $"Answer to complaint {Guid.NewGuid():N}"[..40];

        // Docket a deadline due in 2 days (inside the default 3-day reminder window).
        Guid tenantId;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            tenantId = (await platform.Tenants.FirstAsync(t => t.Slug == "dev")).Id;

            var legal = scope.ServiceProvider.GetRequiredService<LegalDbContext>();
            var matter = new Matter { TenantId = tenantId, Name = $"Docketing eval {Guid.NewGuid():N}"[..30] };
            legal.Matters.Add(matter);
            legal.MatterDeadlines.Add(new MatterDeadline
            {
                TenantId = tenantId,
                MatterId = matter.Id,
                Title = marker,
                DueAt = DateTimeOffset.UtcNow.AddDays(2),
                OwnerUserId = owner,
            });
            await legal.SaveChangesAsync();
        }

        // The Deadlines tab endpoint shows it, flagged as due soon.
        using var client = fixture.ClientFor("system_admin");
        using var response = await client.GetAsync("/api/legal/deadlines");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(marker, body, StringComparison.Ordinal);
        Assert.Contains("Due soon", body, StringComparison.Ordinal);

        // First scan: exactly one reminder lands in the owner's inbox and the latch is set.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await DeadlineReminderService.ScanOnceAsync(scope.ServiceProvider, DateTimeOffset.UtcNow);
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var inbox = await platform.UserNotifications.IgnoreQueryFilters()
                .Where(n => n.UserId == owner && n.Category == "legal.deadline")
                .ToListAsync();
            var reminder = Assert.Single(inbox);
            Assert.Contains(marker, reminder.Title, StringComparison.Ordinal);
        }

        // Second scan: the latch holds — no duplicate notification.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await DeadlineReminderService.ScanOnceAsync(scope.ServiceProvider, DateTimeOffset.UtcNow);
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var count = await platform.UserNotifications.IgnoreQueryFilters()
                .CountAsync(n => n.UserId == owner && n.Category == "legal.deadline");
            Assert.Equal(1, count);
        }
    }

    [Fact]
    public async Task ChatCanListDeadlines_ThroughTheAgent()
    {
        using var client = fixture.ClientFor("system_admin");
        using var chat = await client.PostAsJsonAsync("/api/agui/legal",
            new { messages = new[] { new { id = "m1", role = "user", content = "List the upcoming deadlines" } } });
        chat.EnsureSuccessStatusCode();
        var run = Evals.EvalRun.Parse(await chat.Content.ReadAsStringAsync());

        Assert.DoesNotContain("RUN_ERROR", run.EventTypes);
        Assert.Contains("list_deadlines", run.ToolCalls);
        Assert.DoesNotContain("approval_required", run.CustomEvents); // reading dates needs no approval
    }
}
