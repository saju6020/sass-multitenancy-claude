using System.Text.Json;

namespace LoanProposal.Core.Entities;

/// <summary>
/// Tenant-defined custom fields that extend the loan application data model.
/// Implements the unified field registry concept from the architecture document.
/// All configuration subsystems (rules, templates, reports) reference fields by FieldKey.
/// </summary>
public class CustomField
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Stable machine-readable key, e.g. "gst_registration_number"</summary>
    public string FieldKey { get; private set; } = string.Empty;

    /// <summary>Human-readable label shown in UI</summary>
    public string Label { get; private set; } = string.Empty;

    public CustomFieldType FieldType { get; private set; }
    public bool IsRequired { get; private set; }
    public bool IsSearchable { get; private set; }  // Whether to index for reporting
    public string? ValidationRegex { get; private set; }
    public string? DefaultValue { get; private set; }

    /// <summary>JSON array of allowed values for Select/MultiSelect fields</summary>
    public string? OptionsJson { get; private set; }

    /// <summary>Which loan products this field applies to (null = all)</summary>
    public string? ApplicableProductIdsJson { get; private set; }

    public int DisplayOrder { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTime CreatedAt { get; private set; }

    public Tenant Tenant { get; private set; } = null!;

    private CustomField() { }

    public static CustomField Create(Guid tenantId, string fieldKey, string label,
        CustomFieldType fieldType, bool isRequired = false, bool isSearchable = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldKey);
        // Field keys must be snake_case, no spaces
        if (!System.Text.RegularExpressions.Regex.IsMatch(fieldKey, @"^[a-z][a-z0-9_]*$"))
            throw new ArgumentException($"Field key '{fieldKey}' must be lowercase snake_case.", nameof(fieldKey));

        return new CustomField
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            FieldKey = fieldKey,
            Label = label,
            FieldType = fieldType,
            IsRequired = isRequired,
            IsSearchable = isSearchable,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void SetOptions(IEnumerable<string> options)
    {
        if (FieldType is not (CustomFieldType.Select or CustomFieldType.MultiSelect))
            throw new InvalidOperationException("Options only apply to Select/MultiSelect fields.");
        OptionsJson = JsonSerializer.Serialize(options);
    }

    public IEnumerable<string> GetOptions() =>
        OptionsJson is null ? [] : JsonSerializer.Deserialize<IEnumerable<string>>(OptionsJson)!;

    public void SetApplicableProducts(IEnumerable<Guid> productIds) =>
        ApplicableProductIdsJson = JsonSerializer.Serialize(productIds);
}

public enum CustomFieldType
{
    Text,
    Number,
    Decimal,
    Boolean,
    Date,
    Select,
    MultiSelect
}
