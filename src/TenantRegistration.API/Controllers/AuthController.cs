using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using LoanProposal.Core.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Shared.Contracts;
using TenantRegistration.API.Data;

namespace TenantRegistration.API.Controllers;

[AllowAnonymous]
[Route("auth")]
public class AuthController : Controller
{
    private readonly IConfiguration _configuration;
    private readonly PlatformDbContext _platformDb;
    private readonly PasswordHasher<PlatformUser> _passwordHasher;

    public AuthController(IConfiguration configuration, PlatformDbContext platformDb, PasswordHasher<PlatformUser> passwordHasher)
    {
        _configuration = configuration;
        _platformDb = platformDb;
        _passwordHasher = passwordHasher;
    }

    [HttpGet("/account/login")]
    public IActionResult Login(string? returnUrl = null) => View("Login", new LoginForm { ReturnUrl = returnUrl });

    [HttpPost("/account/login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginForm form, CancellationToken ct)
    {
        var user = await ValidateUserAsync(form.Email, form.Password, ct);
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, "Invalid email or password.");
            return View("Login", form);
        }

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, BuildPrincipal(user));
        if (!string.IsNullOrWhiteSpace(form.ReturnUrl) && Url.IsLocalUrl(form.ReturnUrl))
            return Redirect(form.ReturnUrl);

        return Redirect("/platform/tenants");
    }

    [Authorize]
    [HttpPost("/account/logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/account/login");
    }

    [HttpGet("/account/access-denied")]
    public IActionResult AccessDenied() => View("AccessDenied");

    [HttpPost("token")]
    public async Task<IActionResult> Token([FromBody] TokenRequestDto request, CancellationToken ct)
    {
        var user = await ValidateUserAsync(request.Email, request.Password, ct);
        if (user is null) return Unauthorized(new { error = "Invalid email or password." });

        var expires = DateTime.UtcNow.AddHours(8);
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(GetSigningKey(_configuration))),
            SecurityAlgorithms.HmacSha256);

        var claims = BuildPrincipal(user).Claims.ToList();
        claims.Add(new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()));
        claims.Add(new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()));

        var token = new JwtSecurityToken(
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            expires: expires,
            signingCredentials: credentials);

        return Ok(new
        {
            access_token = new JwtSecurityTokenHandler().WriteToken(token),
            token_type = "Bearer",
            expires_at = expires,
            tenant_id = user.TenantId,
            tenant_slug = user.TenantSlug,
            roles = user.GetRoles()
        });
    }

    private async Task<PlatformUser?> ValidateUserAsync(string email, string password, CancellationToken ct)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var user = await _platformDb.PlatformUsers.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);
        if (user is null || !user.IsActive) return null;

        return _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed
            ? null
            : user;
    }

    internal static ClaimsPrincipal BuildPrincipal(PlatformUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, user.FullName)
        };

        if (user.TenantId.HasValue)
        {
            claims.Add(new Claim(AuthClaimTypes.TenantId, user.TenantId.Value.ToString()));
            claims.Add(new Claim(AuthClaimTypes.TenantSlug, user.TenantSlug));
        }

        claims.AddRange(user.GetRoles().Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static string GetSigningKey(IConfiguration configuration) =>
        configuration["Jwt:SigningKey"] ?? "dev-only-signing-key-change-before-production-32bytes";
}

public class LoginForm
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? ReturnUrl { get; set; }
}
