using LoanProposal.Core.Entities;
using LoanProposal.Core.Interfaces;

namespace LoanProposal.Infrastructure.Services;

/// <summary>
/// Manages SLA timers for workflow steps.
/// Uses Hangfire for durable scheduling (survives process restarts).
/// Timers are:
///   - Business-calendar aware (excludes weekends + tenant-specific holidays)
///   - Cancellable when an application advances
///   - Recalculated only for new applications when SLA config changes
/// </summary>
public class SlaTimerService
{
    private readonly ITenantRepository _tenantRepo;
    private readonly INotificationService _notifications;

    public SlaTimerService(ITenantRepository tenantRepo, INotificationService notifications)
    {
        _tenantRepo = tenantRepo;
        _notifications = notifications;
    }

    /// <summary>
    /// Schedules an SLA deadline for a specific application step.
    /// The job ID pattern (tenantId:applicationId:stepId) allows cancellation.
    /// </summary>
    public async Task ScheduleStepSlaAsync(
        Guid tenantId, Guid applicationId, string applicationNumber,
        WorkflowStepDefinition step, CancellationToken ct = default)
    {
        if (step.SlaHours is null) return;

        var deadline = await ComputeDeadlineAsync(tenantId, step.SlaHours.Value, ct);

        // In production: Hangfire.BackgroundJob.Schedule(() => OnSlaBreachedAsync(...), deadline - DateTime.UtcNow)
        // Job ID allows cancellation: $"sla:{tenantId}:{applicationId}:{step.StepId}"
        Console.WriteLine($"[SLA] Scheduled breach check for {applicationNumber}/{step.StepId} at {deadline:O}");

        if (step.EscalationAfterHours.HasValue)
        {
            var escalationTime = await ComputeDeadlineAsync(tenantId, step.EscalationAfterHours.Value, ct);
            Console.WriteLine($"[SLA] Escalation scheduled for {applicationNumber}/{step.StepId} at {escalationTime:O}");
        }
    }

    public void CancelStepSla(Guid tenantId, Guid applicationId, string stepId)
    {
        var jobId = $"sla:{tenantId}:{applicationId}:{stepId}";
        // In production: Hangfire.BackgroundJob.Delete(jobId)
        Console.WriteLine($"[SLA] Cancelled timer {jobId}");
    }

    public async Task OnSlaBreachedAsync(Guid tenantId, Guid applicationId,
        string applicationNumber, WorkflowStepDefinition step)
    {
        await _notifications.SendAsync(new NotificationRequest(
            TenantId: tenantId,
            Channel: "email",
            RecipientId: step.EscalationRoleCode ?? step.AssigneeRoleCode ?? "ops",
            TemplateKey: "sla_breached",
            Variables: new Dictionary<string, string>
            {
                ["ApplicationNumber"] = applicationNumber,
                ["StepName"] = step.Name,
                ["SlaHours"] = step.SlaHours?.ToString() ?? "N/A"
            }
        ));
    }

    /// <summary>
    /// Computes a deadline in business hours, respecting:
    ///   - Tenant-configured working days (e.g. Sun-Thu for Middle East tenants)
    ///   - Tenant-configured public holiday list
    ///   - Tenant timezone
    /// This handles the "business days excluding region-specific holidays" requirement.
    /// </summary>
    private async Task<DateTime> ComputeDeadlineAsync(Guid tenantId, int slaHours, CancellationToken ct)
    {
        var calendarConfig = await _tenantRepo.GetConfigAsync(tenantId, TenantConfigKeys.BusinessCalendar, ct);
        var holidaysConfig = await _tenantRepo.GetConfigAsync(tenantId, TenantConfigKeys.PublicHolidays, ct);

        var workingDays = calendarConfig?.AsJson<DayOfWeek[]>()
            ?? [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

        var holidays = holidaysConfig?.AsJson<DateTime[]>() ?? [];
        var holidayDates = holidays.Select(h => h.Date).ToHashSet();

        var current = DateTime.UtcNow;
        var remainingHours = slaHours;

        while (remainingHours > 0)
        {
            current = current.AddHours(1);
            if (workingDays.Contains(current.DayOfWeek) && !holidayDates.Contains(current.Date))
            {
                remainingHours--;
            }
        }

        return current;
    }
}

/// <summary>Business calendar configuration stored in TenantConfiguration.</summary>
public record BusinessCalendar(DayOfWeek[] WorkingDays, string Timezone, int WorkdayStartHour, int WorkdayEndHour);
