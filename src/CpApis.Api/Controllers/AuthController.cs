using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CpApis.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public AuthController(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    [AllowAnonymous]
    [HttpPost("token")]
    public async Task<IActionResult> CreateToken([FromBody] PasswordTokenRequest? request, CancellationToken cancellationToken)
    {
        if (request is null ||
            string.IsNullOrWhiteSpace(request.UserName) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid request",
                Detail = "UserName and Password are required."
            });
        }

        var azureAdSection = _configuration.GetRequiredSection("AzureAd");
        var instance = (azureAdSection["Instance"] ?? "https://login.microsoftonline.com/").TrimEnd('/');
        var tenantId = azureAdSection["TenantId"]?.Trim();
        var clientId = azureAdSection["ClientId"]?.Trim();
        var clientSecret = azureAdSection["ClientSecret"]?.Trim();
        var audience = azureAdSection["Audience"]?.Trim();
        var scope = azureAdSection["Scope"]?.Trim();

        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            return Problem(
                title: "Azure AD configuration missing",
                detail: "AzureAd:TenantId, AzureAd:ClientId, and AzureAd:ClientSecret must be configured for api/auth/token.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        audience ??= $"api://{clientId}";
        scope ??= $"{audience}/access_as_user";

        var tokenEndpoint = $"{instance}/{tenantId}/oauth2/v2.0/token";
        var client = _httpClientFactory.CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["grant_type"] = "password",
                ["scope"] = scope,
                ["username"] = request.UserName,
                ["password"] = request.Password
            })
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(message, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var token = await response.Content.ReadFromJsonAsync<EntraTokenSuccessResponse>(cancellationToken: cancellationToken);
            if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
            {
                return Problem(
                    title: "Token response invalid",
                    detail: "Microsoft Entra returned a success response without an access token.",
                    statusCode: StatusCodes.Status502BadGateway);
            }

            return Ok(new
            {
                tokenType = token.TokenType,
                expiresIn = token.ExpiresIn,
                scope = token.Scope,
                accessToken = token.AccessToken
            });
        }

        var error = await response.Content.ReadFromJsonAsync<EntraTokenErrorResponse>(cancellationToken: cancellationToken);
        var detail = error?.ErrorDescription ?? "The username or password was rejected by Microsoft Entra.";
        if (detail.Contains("AADSTS50076", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("AADSTS50079", StringComparison.OrdinalIgnoreCase))
        {
            detail = "This account requires MFA or extra verification. Microsoft password grant cannot be used for that user.";
        }

        return Unauthorized(new ProblemDetails
        {
            Title = "Token request failed",
            Detail = detail,
            Extensions =
            {
                ["error"] = error?.Error,
                ["error_codes"] = error?.ErrorCodes
            }
        });
    }

    public sealed class PasswordTokenRequest
    {
        public string? UserName { get; set; }
        public string? Password { get; set; }
    }

    private sealed class EntraTokenSuccessResponse
    {
        [JsonPropertyName("token_type")]
        public string TokenType { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("scope")]
        public string Scope { get; set; } = string.Empty;

        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;
    }

    private sealed class EntraTokenErrorResponse
    {
        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("error_codes")]
        public int[]? ErrorCodes { get; set; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; set; }
    }
}
