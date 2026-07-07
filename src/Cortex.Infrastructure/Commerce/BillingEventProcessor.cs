using System.Text.Json;
using Cortex.Application.Commerce;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cortex.Infrastructure.Commerce;

/// <summary>
/// The process-safe half of the billing pipeline: drains the <see cref="BillingEvent"/> inbox in
/// the background. Idempotent at every layer — the inbox is unique per event, the entitlement is
/// unique per subscription, and a re-run of a half-processed event converges rather than
/// duplicating. Failures retry with a bound, then dead-letter with the error kept for triage.
/// Runs only when commerce is enabled.
/// </summary>
public sealed class BillingEventProcessor(
    IServiceScopeFactory scopes,
    IOptions<CommerceOptions> options,
    ILogger<BillingEventProcessor> logger) : BackgroundService
{
    private const int MaxAttempts = 5;

    /// <summary>Inbox scan cadence — short enough that a test polling for the outcome finishes fast.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.IsEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Billing inbox drain failed; retrying next cycle.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var provisioning = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();

        var pending = await db.BillingEvents
            .Where(e => e.ProcessedAt == null && e.Attempts < MaxAttempts)
            .OrderBy(e => e.ReceivedAt)
            .Take(20)
            .ToListAsync(ct);

        foreach (var evt in pending)
        {
            try
            {
                await ProcessAsync(db, provisioning, evt, ct);
                evt.ProcessedAt = DateTimeOffset.UtcNow;
                evt.Error = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                evt.Attempts++;
                evt.Error = ex.Message;
                if (evt.Attempts >= MaxAttempts)
                {
                    evt.ProcessedAt = DateTimeOffset.UtcNow; // dead-letter: recorded, no longer retried
                    logger.LogError(ex, "Billing event {EventId} dead-lettered after {Attempts} attempts.", evt.EventId, evt.Attempts);
                }
                else
                {
                    logger.LogWarning(ex, "Billing event {EventId} failed (attempt {Attempts}); will retry.", evt.EventId, evt.Attempts);
                }
            }

            await db.SaveChangesAsync(ct);
        }
    }

    private async Task ProcessAsync(
        PlatformDbContext db, ITenantProvisioningService provisioning, BillingEvent evt, CancellationToken ct)
    {
        switch (evt.Type)
        {
            case "checkout.session.completed":
                await HandleCheckoutCompletedAsync(db, provisioning, evt, ct);
                break;
            default:
                // Lifecycle events (subscription.updated / payment_failed / deleted) are phase 4c;
                // acknowledging unknown types keeps the inbox draining.
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Billing event {EventId} of type {Type} acknowledged (no handler yet).", evt.EventId, evt.Type);
                }
                break;
        }
    }

    /// <summary>
    /// A completed checkout carries OUR provisioning request in the session's metadata (set when
    /// the Checkout Session is created): productId, plan, name, slug, adminEmail — plus optional
    /// adminSubject, modules (comma-separated), seats, monthlyTokenBudget.
    /// </summary>
    private async Task HandleCheckoutCompletedAsync(
        PlatformDbContext db, ITenantProvisioningService provisioning, BillingEvent evt, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(evt.PayloadJson);
        var session = json.RootElement.GetProperty("data").GetProperty("object");
        var subscriptionRef = Str(session, "subscription")
            ?? throw new InvalidOperationException("checkout session has no subscription id.");
        var metadata = session.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object
            ? m
            : throw new InvalidOperationException("checkout session has no metadata.");

        // The entitlement is the idempotency anchor: one subscription = one entitlement = one tenant.
        var entitlement = await db.TenantEntitlements
            .FirstOrDefaultAsync(e => e.SubscriptionRef == subscriptionRef, ct);
        if (entitlement is { TenantId: not null })
        {
            return; // fully processed on a previous attempt/delivery
        }

        entitlement ??= db.TenantEntitlements.Add(new TenantEntitlement
        {
            ProductId = Str(metadata, "productId") ?? "unknown",
            Plan = Str(metadata, "plan") ?? "unknown",
            SubscriptionRef = subscriptionRef,
            CustomerRef = Str(session, "customer"),
            Seats = Int(metadata, "seats"),
        }).Entity;

        var result = await provisioning.ProvisionAsync(new ProvisionTenantCommand(
            Name: Str(metadata, "name") ?? "",
            Slug: Str(metadata, "slug") ?? "",
            AdminEmail: Str(metadata, "adminEmail") ?? "",
            AdminSubject: Str(metadata, "adminSubject"),
            Modules: Str(metadata, "modules")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            MaxSeats: Int(metadata, "seats"),
            MonthlyTokenBudget: Long(metadata, "monthlyTokenBudget")), ct);

        if (result.Error == ProvisionError.SlugTaken)
        {
            // A retry after the provision landed but before the entitlement update was saved —
            // or a genuine collision. Correlate by slug; fail loudly when it isn't ours.
            var slug = Str(metadata, "slug")?.Trim().ToLowerInvariant();
            var existing = await db.Tenants.FirstAsync(t => t.Slug == slug, ct);
            var claimed = await db.TenantEntitlements.AnyAsync(
                e => e.TenantId == existing.Id && e.SubscriptionRef != subscriptionRef, ct);
            if (claimed)
            {
                throw new InvalidOperationException($"slug '{slug}' is already owned by another subscription.");
            }

            entitlement.TenantId = existing.Id;
        }
        else if (!result.Ok)
        {
            throw new InvalidOperationException($"provisioning failed: {result.ErrorDetail}");
        }
        else
        {
            entitlement.TenantId = result.TenantId;
        }

        entitlement.Status = EntitlementStatus.Active;
        entitlement.UpdatedAt = DateTimeOffset.UtcNow;
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Provisioned tenant {TenantId} for subscription {SubscriptionRef} ({Product}/{Plan}).",
                entitlement.TenantId, subscriptionRef, entitlement.ProductId, entitlement.Plan);
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement obj, string name) =>
        int.TryParse(Str(obj, name), out var v) ? v : null;

    private static long? Long(JsonElement obj, string name) =>
        long.TryParse(Str(obj, name), out var v) ? v : null;
}
