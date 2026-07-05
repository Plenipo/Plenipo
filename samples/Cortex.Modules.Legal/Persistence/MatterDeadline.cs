using Cortex.Core.Entities;

namespace Cortex.Modules.Legal.Persistence;

/// <summary>
/// A docketed date on a matter — court dates, filing deadlines, limitation periods. Docketing is
/// the discipline every practice (and its malpractice carrier) runs on: dates live on the matter,
/// surface in the Deadlines tab soonest-first, and fire a reminder notification ahead of time.
/// The reminder is one-shot (<see cref="ReminderSentAt"/>) so the inbox never gets re-noised.
/// </summary>
public sealed class MatterDeadline : TenantEntityBase
{
    public Guid MatterId { get; set; }

    /// <summary>What is due (e.g. "Answer to complaint", "Discovery cutoff").</summary>
    public required string Title { get; set; }

    /// <summary>When it is due (UTC).</summary>
    public DateTimeOffset DueAt { get; set; }

    public string? Notes { get; set; }

    /// <summary>Marked done — completed deadlines leave the upcoming list and never remind.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Who gets the reminder — the user who docketed it.</summary>
    public Guid? OwnerUserId { get; set; }

    /// <summary>How many days before <see cref="DueAt"/> the reminder fires.</summary>
    public int ReminderDaysBefore { get; set; } = 3;

    /// <summary>Set when the reminder notification was produced, so it fires exactly once.</summary>
    public DateTimeOffset? ReminderSentAt { get; set; }

    /// <summary>Whether the reminder should fire now: open, not yet reminded, inside the window.</summary>
    public bool IsReminderDue(DateTimeOffset now) =>
        CompletedAt is null &&
        ReminderSentAt is null &&
        DueAt <= now.AddDays(ReminderDaysBefore);
}
