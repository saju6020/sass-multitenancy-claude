using LoanProposal.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LoanProposal.Infrastructure.Services;

/// <summary>
/// Development notification adapter that records notification intents without
/// requiring an external email, SMS, or in-app messaging provider.
/// </summary>
public class LoggingNotificationService : INotificationService
{
    private readonly ILogger<LoggingNotificationService> _logger;

    public LoggingNotificationService(ILogger<LoggingNotificationService> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(NotificationRequest request, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Notification queued. TenantId={TenantId}, Channel={Channel}, RecipientId={RecipientId}, TemplateKey={TemplateKey}",
            request.TenantId,
            request.Channel,
            request.RecipientId,
            request.TemplateKey);

        return Task.CompletedTask;
    }
}
