using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using LoanProposal.API.Models;
using LoanProposal.Core.Entities;
using LoanProposal.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace LoanProposal.API.Controllers;

[AllowAnonymous]
[Route("account")]
public class AccountController : Controller
{
    private readonly PlatformDbContext _platformDb;
    private readonly PasswordHasher<PlatformUser> _passwordHasher;

    public AccountController(PlatformDbContext platformDb, PasswordHasher<PlatformUser> passwordHasher)
    {
        _platformDb = platformDb;
        _passwordHasher = passwordHasher;
    }

    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = null) => View(new LoginViewModel { ReturnUrl = returnUrl });

    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken ct)
    {
        var user = await _platformDb.PlatformUsers.FirstOrDefaultAsync(u => u.Email == model.Email.Trim().ToLower(), ct);
        if (user is null || !user.IsActive || _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, model.Password) == PasswordVerificationResult.Failed)
        {
            ModelState.AddModelError(string.Empty, "Invalid email or password.");
            return View(model);
        }

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, BuildPrincipal(user));
        if (!string.IsNullOrWhiteSpace(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
            return Redirect(model.ReturnUrl);

        return RedirectToAction("Index", "Dashboard");
    }

    [Authorize]
    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet("access-denied")]
    public IActionResult AccessDenied() => View();

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
            claims.Add(new Claim("tenant_id", user.TenantId.Value.ToString()));
            claims.Add(new Claim("tenant_slug", user.TenantSlug));
        }

        claims.AddRange(user.GetRoles().Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}

[ApiController]
[AllowAnonymous]
[Route("auth")]
public class TokenController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly PlatformDbContext _platformDb;
    private readonly PasswordHasher<PlatformUser> _passwordHasher;

    public TokenController(IConfiguration configuration, PlatformDbContext platformDb, PasswordHasher<PlatformUser> passwordHasher)
    {
        _configuration = configuration;
        _platformDb = platformDb;
        _passwordHasher = passwordHasher;
    }

    [HttpPost("token")]
    public async Task<IActionResult> Token([FromBody] TokenRequest request, CancellationToken ct)
    {
        var user = await _platformDb.PlatformUsers.FirstOrDefaultAsync(u => u.Email == request.Email.Trim().ToLower(), ct);
        if (user is null || !user.IsActive || _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            return Unauthorized(new { error = "Invalid email or password." });

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(GetSigningKey(_configuration)));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expires = DateTime.UtcNow.AddHours(8);

        var claims = AccountController.BuildPrincipal(user).Claims.ToList();
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

    public static string GetSigningKey(IConfiguration configuration) =>
        configuration["Jwt:SigningKey"]
        ?? "dev-only-signing-key-change-before-production-32bytes";
}

public record TokenRequest(string Email, string Password);
