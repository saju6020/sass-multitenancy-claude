using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json.Serialization;
using LoanProposal.API.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Contracts;

namespace LoanProposal.API.Controllers;

/// <summary>
/// Local MVC sign-in bridge. User/password validation is delegated to TenantRegistration.
/// LoanProposal stores only a local cookie containing the IDP-issued claims.
/// </summary>
[AllowAnonymous]
[Route("account")]
public class AccountController : Controller
{
    private readonly HttpClient _httpClient;

    public AccountController(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("TenantRegistration");
    }

    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = null) => View(new LoginViewModel { ReturnUrl = returnUrl });

    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync("/auth/token", new TokenRequestDto(model.Email, model.Password), ct);
        }
        catch (HttpRequestException)
        {
            ModelState.AddModelError(string.Empty, "TenantRegistration service is unavailable. Start it and try again.");
            return View(model);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            ModelState.AddModelError(string.Empty, "TenantRegistration service did not respond in time.");
            return View(model);
        }

        if (!response.IsSuccessStatusCode)
        {
            ModelState.AddModelError(string.Empty, "Invalid email or password.");
            return View(model);
        }

        TokenLoginResponse? token;
        try
        {
            token = await response.Content.ReadFromJsonAsync<TokenLoginResponse>(cancellationToken: ct);
        }
        catch (HttpRequestException)
        {
            ModelState.AddModelError(string.Empty, "TenantRegistration returned an incomplete login response.");
            return View(model);
        }
        catch (System.Text.Json.JsonException)
        {
            ModelState.AddModelError(string.Empty, "TenantRegistration returned an invalid login response.");
            return View(model);
        }

        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            ModelState.AddModelError(string.Empty, "The identity provider did not return a valid token.");
            return View(model);
        }

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, BuildPrincipal(token.AccessToken));
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

    internal static ClaimsPrincipal BuildPrincipal(string jwt)
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(jwt);
        var claims = token.Claims.Select(c =>
            c.Type == "role" ? new Claim(ClaimTypes.Role, c.Value) :
            c.Type == JwtRegisteredClaimNames.UniqueName ? new Claim(ClaimTypes.Name, c.Value) :
            c).ToList();

        claims.Add(new Claim("access_token", jwt));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}

public class TokenLoginResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;
    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = string.Empty;
    [JsonPropertyName("expires_at")]
    public DateTime ExpiresAt { get; set; }
    [JsonPropertyName("tenant_id")]
    public Guid? TenantId { get; set; }
    [JsonPropertyName("tenant_slug")]
    public string TenantSlug { get; set; } = string.Empty;
    [JsonPropertyName("roles")]
    public IReadOnlyList<string> Roles { get; set; } = [];
}
