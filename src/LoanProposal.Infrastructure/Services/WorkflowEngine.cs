using LoanProposal.Core.Entities;
using LoanProposal.Core.Interfaces;

namespace LoanProposal.Infrastructure.Services;

/// <summary>
/// Executes the tenant-configured workflow for a loan application.
/// Reads step definitions and routing rules from the WorkflowDefinition JSON,
/// evaluates conditional branches, enforces SLAs, and triggers notifications.
/// </summary>
public class WorkflowEngine : IWorkflowEngine
{
    private readonly IRuleEngine _ruleEngine;
    private readonly INotificationService _notifications;
    private readonly IWorkflowDefinitionRepository _workflowRepo;
    private readonly ICustomFieldRepository _customFieldRepo;

    public WorkflowEngine(
        IRuleEngine ruleEngine,
        INotificationService notifications,
        IWorkflowDefinitionRepository workflowRepo,
        ICustomFieldRepository customFieldRepo)
    {
        _ruleEngine = ruleEngine;
        _notifications = notifications;
        _workflowRepo = workflowRepo;
        _customFieldRepo = customFieldRepo;
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

        // Build the shared context for rule evaluation
        var context = await BuildContextAsync(application, ct);

        // Determine next step via routing rules
        var nextStepId = await ResolveNextStepAsync(application, workflow, ct);

        // Check if the next step has a bypass condition
        var nextStep = steps.FirstOrDefault(s => s.StepId == nextStepId);
        if (nextStep?.BypassConditionExpression is not null)
        {
            var bypassResults = await _ruleEngine.EvaluateAsync(
                RuleCategory.StepBypass, context, application.LoanProductId, ct);

            if (bypassResults.Any(r => r.Passed && r.RuleName.Contains(nextStepId)))
            {
                // Skip the bypassed step — recurse to next
                application.AdvanceTo(nextStepId, performedBy, $"[AUTO-BYPASSED] {comments}");
                return await AdvanceAsync(application, action, performedBy,
                    $"[BYPASSED: {nextStep.Name}]", ct);
            }
        }

        application.AdvanceTo(nextStepId!, performedBy, comments);

        // Trigger notifications for the new step
        var notificationsTriggered = await TriggerStepNotificationsAsync(application, nextStep!, ct);

        return new WorkflowAdvanceResult(true, nextStepId, null, notificationsTriggered);
    }

    /// <summary>
    /// Evaluates routing rules in priority order and returns the first matching next step.
    /// Implements the conditional branching described in the architecture document.
    /// Example: "if LoanAmount > 500000 → CreditCommittee, else BranchManagerApproval"
    /// </summary>
    public async Task<string> ResolveNextStepAsync(
        LoanApplication application, WorkflowDefinition workflow, CancellationToken ct = default)
    {
        var rules = workflow.GetRoutingRules()
            .Where(r => r.FromStepId == application.CurrentStepId)
            .OrderBy(r => r.Priority)
            .ToList();

        foreach (var rule in rules)
        {
            if (rule.ConditionExpression is null)
            {
                // Default / unconditional route
                return rule.ToStepId;
            }

            // Evaluate expression against application context
            var result = EvaluateRoutingExpression(rule.ConditionExpression, application);
            if (result) return rule.ToStepId;
        }

        throw new InvalidOperationException(
            $"No routing rule matched for step '{application.CurrentStepId}' in workflow '{workflow.Name}'.");
    }

    /// <summary>
    /// Evaluates a simple routing expression against the application.
    /// In production, replace with a proper sandboxed expression evaluator
    /// (e.g. JsonLogic, NCalc with restricted context, or a custom grammar).
    /// </summary>
    private static bool EvaluateRoutingExpression(string expression, LoanApplication application)
    {
        // Simplified example — production would use a proper sandboxed evaluator
        // Expression format: "LoanAmount > 500000"
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

        // Fallback: custom field expressions e.g. "CustomField['gst_registered'] == true"
        if (expression.StartsWith("CustomField['"))
        {
            var fieldKey = expression.Split('\'')[1];
            var value = application.GetCustomField<bool>(fieldKey);
            return value;
        }

        return false; // Unknown expression — do not route
    }

    private async Task<IReadOnlyList<string>> TriggerStepNotificationsAsync(
        LoanApplication application, WorkflowStepDefinition step, CancellationToken ct)
    {
        var triggered = new List<string>();

        // Notify assignee that a step is pending their action
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

    private async Task<LoanApplicationContext> BuildContextAsync(LoanApplication application, CancellationToken ct)
    {
        // In a real implementation, load full applicant and product from repositories
        // For brevity, we'll construct the context shell here
        var customFields = application
            .GetType()
            .GetProperty("CustomDataJson")!
            .GetValue(application) as string ?? "{}";

        var fieldDict = System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, object?>>(customFields)
            ?? new Dictionary<string, object?>();

        return new LoanApplicationContext(
            application,
            application.Applicant,
            application.LoanProduct,
            fieldDict
        );
    }
}
