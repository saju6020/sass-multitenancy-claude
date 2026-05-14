using System.Text.Json;
using LoanProposal.Core.Entities;
using LoanProposal.Core.Interfaces;

namespace LoanProposal.Infrastructure.Services;

/// <summary>
/// Rule engine that evaluates tenant-configured rules using JSON Logic expressions.
/// JSON Logic provides a safe, sandboxed, serializable rule format that is:
///   - Expressive enough for real business rules (AND/OR, comparisons, arithmetic)
///   - Cannot cause infinite loops or access external systems
///   - Human-readable and auditable
///   - Conflict-checkable at save time
///
/// Example rule expression:
///   {"and": [{">=": [{"var": "applicant.creditScore"}, 620]}, {"<=": [{"var": "applicant.dtiRatio"}, 0.45]}]}
/// </summary>
public class JsonLogicRuleEngine : IRuleEngine
{
    private readonly IRuleDefinitionRepository _ruleRepo;

    public JsonLogicRuleEngine(IRuleDefinitionRepository ruleRepo)
    {
        _ruleRepo = ruleRepo;
    }

    public async Task<IReadOnlyList<RuleEvaluationResult>> EvaluateAsync(
        RuleCategory category,
        LoanApplicationContext context,
        Guid? productId = null,
        CancellationToken ct = default)
    {
        var rules = productId.HasValue
            ? await _ruleRepo.GetApplicableToProductAsync(productId.Value, ct)
            : await _ruleRepo.GetByCategoryAsync(category, ct);

        var dataContext = BuildDataContext(context);
        var results = new List<RuleEvaluationResult>();

        foreach (var rule in rules.Where(r => r.Category == category))
        {
            bool passed;
            try
            {
                passed = EvaluateJsonLogic(rule.Expression, dataContext);
            }
            catch (Exception ex)
            {
                // Rule evaluation failure is logged but does not block processing
                // In production: log to structured logging with TenantId, RuleId, ApplicationId
                Console.Error.WriteLine($"[RuleEngine] Rule '{rule.Name}' evaluation failed: {ex.Message}");
                passed = false;
            }

            results.Add(new RuleEvaluationResult(
                RuleId: rule.Id,
                RuleName: rule.Name,
                Passed: passed,
                Outcome: passed ? rule.OutcomeWhenTrue : null,
                OutcomeData: passed ? rule.OutcomeData : null,
                Expression: rule.Expression
            ));
        }

        return results.AsReadOnly();
    }

    /// <summary>
    /// Builds a flat data context dictionary from the loan application context.
    /// This is the unified data model all rules operate against —
    /// both standard fields and custom fields use the same context structure.
    /// </summary>
    private static Dictionary<string, object?> BuildDataContext(LoanApplicationContext context)
    {
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            // Standard applicant fields
            ["applicant.creditScore"] = context.Applicant?.CreditScore,
            ["applicant.dtiRatio"] = context.Applicant?.DebtToIncomeRatio,
            ["applicant.annualIncome"] = context.Applicant?.AnnualIncome,
            ["applicant.relationshipYears"] = context.Applicant?.RelationshipYears,

            // Standard application fields
            ["application.requestedAmount"] = context.Application.RequestedAmount,
            ["application.tenureMonths"] = context.Application.RequestedTenureMonths,

            // Product context
            ["product.type"] = context.Product?.ProductType.ToString(),
        };

        // Merge custom fields — all keyed as "custom.{fieldKey}"
        // This ensures custom fields live in the same data context as standard fields,
        // fulfilling the "unified field registry" architecture requirement.
        foreach (var (key, value) in context.CustomFields)
        {
            data[$"custom.{key}"] = value;
        }

        return data;
    }

    /// <summary>
    /// Evaluates a JSON Logic expression against a data context.
    /// In production, replace this stub with a proper JSON Logic library
    /// (e.g. JsonLogic.Net or a custom implementation).
    ///
    /// JSON Logic reference: https://jsonlogic.com
    /// Supported operators: ==, !=, >, >=, <, <=, and, or, not, var, if, +, -, *, /
    /// </summary>
    private static bool EvaluateJsonLogic(string expressionJson, Dictionary<string, object?> data)
    {
        // STUB: Replace with real JsonLogic evaluator in production
        // Example: JsonLogic.Net.JsonLogicEvaluator.Evaluate(expression, dataElement)

        // This simplified implementation handles basic comparison rules for illustration
        try
        {
            var expression = JsonDocument.Parse(expressionJson).RootElement;
            return EvaluateElement(expression, data);
        }
        catch
        {
            return false;
        }
    }

    private static bool EvaluateElement(JsonElement element, Dictionary<string, object?> data)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;

        foreach (var prop in element.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "and":
                    return prop.Value.EnumerateArray().All(e => EvaluateElement(e, data));
                case "or":
                    return prop.Value.EnumerateArray().Any(e => EvaluateElement(e, data));
                case ">=":
                {
                    var args = prop.Value.EnumerateArray().ToList();
                    var left = ResolveValue(args[0], data);
                    var right = ResolveValue(args[1], data);
                    return Compare(left, right) >= 0;
                }
                case "<=":
                {
                    var args = prop.Value.EnumerateArray().ToList();
                    var left = ResolveValue(args[0], data);
                    var right = ResolveValue(args[1], data);
                    return Compare(left, right) <= 0;
                }
                case ">":
                {
                    var args = prop.Value.EnumerateArray().ToList();
                    var left = ResolveValue(args[0], data);
                    var right = ResolveValue(args[1], data);
                    return Compare(left, right) > 0;
                }
                case "<":
                {
                    var args = prop.Value.EnumerateArray().ToList();
                    var left = ResolveValue(args[0], data);
                    var right = ResolveValue(args[1], data);
                    return Compare(left, right) < 0;
                }
                case "==":
                {
                    var args = prop.Value.EnumerateArray().ToList();
                    var left = ResolveValue(args[0], data);
                    var right = ResolveValue(args[1], data);
                    return left?.ToString() == right?.ToString();
                }
            }
        }
        return false;
    }

    private static object? ResolveValue(JsonElement element, Dictionary<string, object?> data)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("var", out var varProp))
        {
            var key = varProp.GetString()!;
            return data.TryGetValue(key, out var val) ? val : null;
        }
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDecimal(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static int Compare(object? a, object? b)
    {
        if (a is decimal da && b is decimal db) return da.CompareTo(db);
        if (a is int ia && b is int ib) return ia.CompareTo(ib);
        return string.Compare(a?.ToString(), b?.ToString(), StringComparison.Ordinal);
    }
}
