using System.Text.Json;
using System.Text.Json.Nodes;
using CpApis.Api.Options;
using CpApis.Api.Services;
using CpApis.Shared.Runtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CpApis.Api.Controllers;

/// <summary>
/// Read-only CardPointe Gateway access. Inquiry, settlement status, funding, and merchant
/// info only, so AI clients can observe payment activity but never move money. Money-moving
/// endpoints (auth, capture, refund, void, profile delete, batch open/close) are excluded by
/// design and must not be added here. The gateway masks card numbers in its responses;
/// credentials never appear in any response.
/// </summary>
[ApiController]
[Authorize]
[Route("api/cardpointe")]
public sealed class CardPointeController : ControllerBase
{
    private readonly CardPointeGatewayClient _gateway;
    private readonly CardPointeOptions _options;

    public CardPointeController(CardPointeGatewayClient gateway, IOptions<CardPointeOptions> options)
    {
        _gateway = gateway;
        _options = options.Value;
    }

    /// <summary>Transaction inquiry by retrieval reference number (retref).</summary>
    [HttpGet("inquire/{retref}")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Inquire(string retref)
    {
        return await GatewayCall(() => _gateway.InquireAsync(retref));
    }

    /// <summary>Settlement status for a month (YYYYMM) or one day (YYYYMMDD). Monthly requests return one result per calendar day.</summary>
    [HttpGet("settlestat")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SettleStat([FromQuery] string? date)
    {
        var period = CardPointeReportingPeriod.Expand(GetPeriod(date));
        if (period is null)
            return BadRequest("date is required in YYYYMM format (YYYYMMDD is also accepted for one day).");

        return period.Count == 1
            ? await GatewayCall(() => _gateway.SettleStatAsync(period[0]))
            : await MonthlyGatewayCall(period, day => _gateway.SettleStatAsync(day));
    }

    /// <summary>Funding (deposit) report for a month (YYYYMM) or one day (YYYYMMDD). Monthly requests return one result per calendar day.</summary>
    [HttpGet("funding")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Funding([FromQuery] string? date)
    {
        var period = CardPointeReportingPeriod.Expand(GetPeriod(date));
        if (period is null)
            return BadRequest("date is required in YYYYMM format (YYYYMMDD is also accepted for one day).");

        return period.Count == 1
            ? await GatewayCall(() => _gateway.FundingAsync(period[0]))
            : await MonthlyGatewayCall(period, day => _gateway.FundingAsync(day));
    }

    /// <summary>Merchant profile inquiry for the configured merchant.</summary>
    [HttpGet("merchant")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Merchant()
    {
        return await GatewayCall(() => _gateway.InquireMerchantAsync());
    }

    private async Task<IActionResult> GatewayCall(Func<Task<string>> call)
    {
        if (_options.IsConfigured is false)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "CardPointe gateway is not configured on this host.");
        }

        try
        {
            var json = await call();
            return Content(json, "application/json");
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("CardPointe gateway call failed", StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
    }

    private string? GetPeriod(string? date) =>
        string.IsNullOrWhiteSpace(date)
            ? Request.Query["yearMonth"].FirstOrDefault()
            : date;

    private async Task<IActionResult> MonthlyGatewayCall(
        IReadOnlyList<string> days,
        Func<string, Task<string>> call)
    {
        var results = new List<object>(days.Count);
        var successfulDays = 0;
        foreach (var day in days)
        {
            try
            {
                var body = await call(day);
                JsonNode data;
                try
                {
                    data = JsonNode.Parse(body) ?? JsonValue.Create(body)!;
                }
                catch (JsonException)
                {
                    data = JsonValue.Create(body)!;
                }

                results.Add(new { date = day, status = "ok", data });
                successfulDays++;
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("CardPointe gateway call failed", StringComparison.Ordinal))
            {
                results.Add(new { date = day, status = "error", error = ex.Message });
            }
        }

        return new JsonResult(new
        {
            yearMonth = days[0][..6],
            totalDays = days.Count,
            successfulDays,
            days = results
        });
    }
}
