namespace LoanProposal.Core.Entities;

/// <summary>
/// Stores per-tenant configuration as typed key-value pairs.
/// Covers: SLA windows, escalation rules, notification channels, business calendar, etc.
/// </summary>
public class TenantConfiguration
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string Value { get; private set; } = string.Empty;
    public ConfigValueType ValueType { get; private set; }
    public string? Description { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public string UpdatedBy { get; private set; } = string.Empty;

    public Tenant Tenant { get; private set; } = null!;

    private TenantConfiguration() { }

    public static TenantConfiguration Create(Guid tenantId, string key, string value,
        ConfigValueType valueType, string updatedBy, string? description = null)
    {
        return new TenantConfiguration
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Key = key,
            Value = value,
            ValueType = valueType,
            Description = description,
            UpdatedAt = DateTime.UtcNow,
            UpdatedBy = updatedBy
        };
    }

    public void Update(string newValue, string updatedBy)
    {
        Value = newValue;
        UpdatedAt = DateTime.UtcNow;
        UpdatedBy = updatedBy;
    }

    // Typed accessors
    public int AsInt() => int.Parse(Value);
    public bool AsBool() => bool.Parse(Value);
    public decimal AsDecimal() => decimal.Parse(Value);
    public T AsJson<T>() => System.Text.Json.JsonSerializer.Deserialize<T>(Value)!;
}

public enum ConfigValueType { String, Integer, Boolean, Decimal, Json }

/// <summary>
/// Well-known configuration keys — prevents magic strings throughout the codebase.
/// </summary>
public static class TenantConfigKeys
{
    public const string SlaFastTrackHours = "sla.fast_track_hours";
    public const string SlaStandardHours = "sla.standard_hours";
    public const string SlaEscalationDays = "sla.escalation_days";
    public const string FastTrackMaxAmount = "workflow.fast_track_max_amount";
    public const string BusinessCalendar = "calendar.business_days_json";
    public const string PublicHolidays = "calendar.public_holidays_json";
    public const string NotificationChannels = "notifications.channels_json";
    public const string CreditScoreThreshold = "rules.credit_score_min";
    public const string DtiRatioMax = "rules.dti_ratio_max";
    public const string RelationshipYearsForBypass = "workflow.relationship_bypass_years";
}
