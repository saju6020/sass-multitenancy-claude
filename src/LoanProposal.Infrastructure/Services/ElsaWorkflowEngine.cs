using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Options;
using LoanProposal.Core.Entities;
using LoanProposal.Core.Interfaces;

namespace LoanProposal.Infrastructure.Services;

/// <summary>
/// Executes tenant-configured loan workflows through Elsa while keeping the
/// application's WorkflowDefinition entity as the tenant-owned configuration store.
/// </summary>
public class ElsaWorkflowEngine : IWorkflowEngine
{
    private readonly IWorkflowRunner _workflowRunner;
    private readonly INotificationService _notifications;
    private readonly IWorkflowDefinitionRepository _workflowRepo;

    public ElsaWorkflowEngine(
        IWorkflowRunner workflowRunner,
        INotificationService notifications,
        IWorkflowDefinitionRepository workflowRepo)
    {
        _workflowRunner = workflowRunner;
        _notifications = notifications;
        _workflowRepo = workflowRepo;
    }

    public async Task<WorkflowAdvanceResult> AdvanceAsync(
        LoanApplication application, string action, string performedBy,
        string? comments = null, CancellationToken ct = default)
    {
        var workflow = await _workflowRepo.GetByIdAsync(application.WorkflowDefinitionId, ct);
        if (workflow is null)
            return new WorkflowAdvanceResult(false, null, "Workflow definition not found.", []);

        var steps = workflow.GetSteps();
        var currentStep = steps.FirstOrDefault(s => s.StepId == application.CurrentStepId);
        if (currentStep is null)
            return new WorkflowAdvanceResult(false, null, $"Step '{application.CurrentStepId}' not found in workflow.", []);

        string nextStepId;
        try
        {
            nextStepId = await ResolveNextStepAsync(application, workflow, ct);
        }
        catch (InvalidOperationException ex)
        {
            return new WorkflowAdvanceResult(false, null, ex.Message, []);
        }

        var nextStep = steps.FirstOrDefault(s => s.StepId == nextStepId);
        if (nextStep is null)
            return new WorkflowAdvanceResult(false, null, $"Step '{nextStepId}' not found in workflow.", []);

        await RunElsaTransitionWorkflowAsync(application, workflow, currentStep, nextStep, action, performedBy, comments, ct);

        application.AdvanceTo(nextStepId, performedBy, comments);
        var notificationsTriggered = await TriggerStepNotificationsAsync(application, nextStep, ct);

        return new WorkflowAdvanceResult(true, nextStepId, null, notificationsTriggered);
    }

    public Task<string> ResolveNextStepAsync(
        LoanApplication application, WorkflowDefinition workflow, CancellationToken ct = default)
    {
        var rules = workflow.GetRoutingRules()
            .Where(r => r.FromStepId == application.CurrentStepId)
            .OrderBy(r => r.Priority)
            .ToList();

        foreach (var rule in rules)
        {
            if (rule.ConditionExpression is null)
                return Task.FromResult(rule.ToStepId);

            if (EvaluateRoutingExpression(rule.ConditionExpression, application))
                return Task.FromResult(rule.ToStepId);
        }

        throw new InvalidOperationException(
            $"No routing rule matched for step '{application.CurrentStepId}' in workflow '{workflow.Name}'.");
    }

    private async Task RunElsaTransitionWorkflowAsync(
        LoanApplication application,
        WorkflowDefinition workflow,
        WorkflowStepDefinition currentStep,
        WorkflowStepDefinition nextStep,
        string action,
        string performedBy,
        string? comments,
        CancellationToken ct)
    {
        var elsaWorkflow = new Sequence
        {
            Activities =
            {
                new WriteLine($"Tenant workflow: {workflow.Name} v{workflow.Version}"),
                new WriteLine($"Application: {application.ApplicationNumber}"),
                new WriteLine($"Action: {action} by {performedBy}"),
                new WriteLine($"Transition: {currentStep.StepId} -> {nextStep.StepId}"),
                new WriteLine($"Comments: {comments ?? "N/A"}")
            }
        };

        await _workflowRunner.RunAsync(elsaWorkflow, new RunWorkflowOptions(), ct);
    }

    private static bool EvaluateRoutingExpression(string expression, LoanApplication application)
    {
        if (expression.Contains("LoanAmount >"))
        {
            var threshold = decimal.Parse(expression.Split('>')[1].Trim());
            return application.RequestedAmount > threshold;
        }

        if (expression.Contains("LoanAmount <="))
        {
            var threshold = decimal.Parse(expression.Split("<=")[1].Trim());
            return application.RequestedAmount <= threshold;
        }

        if (expression.StartsWith("CustomField['"))
        {
            var fieldKey = expression.Split('\'')[1];
            return application.GetCustomField<bool>(fieldKey);
        }

        return false;
    }

    private async Task<IReadOnlyList<string>> TriggerStepNotificationsAsync(
        LoanApplication application, WorkflowStepDefinition step, CancellationToken ct)
    {
        var triggered = new List<string>();

        if (step.AssigneeRoleCode is not null)
        {
            await _notifications.SendAsync(new NotificationRequest(
                TenantId: application.TenantId,
                Channel: "email",
                RecipientId: step.AssigneeRoleCode,
                TemplateKey: "step_assigned",
                Variables: new Dictionary<string, string>
                {
                    ["ApplicationNumber"] = application.ApplicationNumber,
                    ["StepName"] = step.Name,
                    ["DueIn"] = step.SlaHours?.ToString() ?? "N/A"
                }
            ), ct);
            triggered.Add($"step_assigned:{step.AssigneeRoleCode}");
        }

        return triggered;
    }
}