using System.Reflection;
using System.Text.RegularExpressions;
using LoanProposal.Core.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts;
using TenantRegistration.API.Data;
using TenantRegistration.API.Models;
using TenantRegistration.API.Services;

namespace TenantRegistration.API.Controllers;

[Authorize(Policy = RoleNames.PlatformAdmin)]
[Route("platform/tenants")]
public class TenantsController : Controller
{
    private readonly PlatformDbContext _platformDb;
    private readonly TenantDatabaseNameBuilder _tenantDatabaseNameBuilder;

    public TenantsController(PlatformDbContext platformDb, TenantDatabaseNameBuilder tenantDatabaseNameBuilder)
    {
        _platformDb = platformDb;
        _tenantDatabaseNameBuilder = tenantDatabaseNameBuilder;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var tenants = await _platformDb.Tenants.AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new TenantSummaryViewModel
            {
                Id = t.Id,
                Name = t.Name,
                Slug = t.Slug,
                Currency = t.DefaultCurrency,
                Timezone = t.DefaultTimezone,
                DatabaseName = t.DatabaseName,
                IsActive = t.IsActive,
                CreatedAt = t.CreatedAt
            })
            .ToListAsync(ct);

        return View(new TenantListViewModel { Tenants = tenants });
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateTenantForm form, CancellationToken ct)
    {
        if (!ValidateTenantForm(form)) return RedirectToAction(nameof(Index));

        var slug = NormalizeSlug(form.Slug);
        if (string.IsNullOrWhiteSpace(slug))
        {
            TempData["Error"] = "Slug must contain at least one letter or number.";
            return RedirectToAction(nameof(Index));
        }

        if (await _platformDb.Tenants.AnyAsync(t => t.Slug == slug, ct))
        {
            TempData["Error"] = $"Slug '{slug}' is already taken.";
            return RedirectToAction(nameof(Index));
        }

        var tenant = Tenant.Create(form.Name.Trim(), slug, form.Currency.Trim().ToUpperInvariant(), form.Timezone.Trim());
        tenant.ConfigureDatabase(_tenantDatabaseNameBuilder.BuildDatabaseName(slug), _tenantDatabaseNameBuilder.BuildConnectionString(slug));

        _platformDb.Tenants.Add(tenant);
        await _platformDb.SaveChangesAsync(ct);

        TempData["Success"] = $"Tenant '{tenant.Name}' created with LoanProposal database metadata.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("{id:guid}/edit")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var tenant = await _platformDb.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound();

        return View(new TenantEditForm
        {
            Id = tenant.Id,
            Name = tenant.Name,
            Slug = tenant.Slug,
            Currency = tenant.DefaultCurrency,
            Timezone = tenant.DefaultTimezone,
            IsActive = tenant.IsActive,
            DatabaseName = tenant.DatabaseName,
            DatabaseConnectionString = tenant.DatabaseConnectionString
        });
    }

    [HttpPost("{id:guid}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, TenantEditForm form, CancellationToken ct)
    {
        var tenant = await _platformDb.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound();
        if (!ValidateTenantForm(form)) return View(form);

        var slug = NormalizeSlug(form.Slug);
        if (string.IsNullOrWhiteSpace(slug))
        {
            ModelState.AddModelError(nameof(form.Slug), "Slug must contain at least one letter or number.");
            return View(form);
        }

        if (await _platformDb.Tenants.AnyAsync(t => t.Id != id && t.Slug == slug, ct))
        {
            ModelState.AddModelError(nameof(form.Slug), $"Slug '{slug}' is already taken.");
            return View(form);
        }

        Set(tenant, nameof(Tenant.Name), form.Name.Trim());
        Set(tenant, nameof(Tenant.Slug), slug);
        Set(tenant, nameof(Tenant.DefaultCurrency), form.Currency.Trim().ToUpperInvariant());
        Set(tenant, nameof(Tenant.DefaultTimezone), form.Timezone.Trim());
        Set(tenant, nameof(Tenant.IsActive), form.IsActive);

        var databaseName = string.IsNullOrWhiteSpace(form.DatabaseName)
            ? _tenantDatabaseNameBuilder.BuildDatabaseName(slug)
            : form.DatabaseName.Trim();
        var connectionString = string.IsNullOrWhiteSpace(form.DatabaseConnectionString)
            ? _tenantDatabaseNameBuilder.BuildConnectionString(slug)
            : form.DatabaseConnectionString.Trim();
        tenant.ConfigureDatabase(databaseName, connectionString);

        await _platformDb.SaveChangesAsync(ct);
        TempData["Success"] = $"Tenant '{tenant.Name}' updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tenant = await _platformDb.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound();

        _platformDb.Tenants.Remove(tenant);
        await _platformDb.SaveChangesAsync(ct);

        TempData["Success"] = "Tenant deleted from TenantRegistration registry. Service databases were left intact.";
        return RedirectToAction(nameof(Index));
    }

    private bool ValidateTenantForm(CreateTenantForm form)
    {
        if (!string.IsNullOrWhiteSpace(form.Name) && !string.IsNullOrWhiteSpace(form.Slug)) return true;
        TempData["Error"] = "Tenant name and slug are required.";
        return false;
    }

    private static string NormalizeSlug(string slug) =>
        Regex.Replace(slug.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    private static void Set<T>(object target, string propertyName, T value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Property '{propertyName}' was not found on {target.GetType().Name}.");
        property.SetValue(target, value);
    }
}
