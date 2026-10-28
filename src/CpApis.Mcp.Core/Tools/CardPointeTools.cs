using System.ComponentModel;
using CpApis.Mcp.Core.Services;
using ModelContextProtocol.Server;

namespace CpApis.Mcp.Core.Tools;

/// <summary>
/// Read-only CardPointe tools for AI clients. Inquiry, settlement status, funding, and
/// merchant info only. There are intentionally no tools to authorize, capture, refund,
/// void, or manage payment profiles, so an AI session can observe payment activity but
/// never move money.
/// </summary>
[McpServerToolType]
public sealed class CardPointeTools
{
    private readonly CardPointeApiClient _apiClient;

    public CardPointeTools(CardPointeApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    [McpServerTool(Name = "cardpointe_inquire")]
    [Description("Look up one CardPointe payment transaction by its retrieval reference number (retref). Read-only.")]
    public Task<string> InquireAsync(string retref) => _apiClient.InquireAsync(retref);

    [McpServerTool(Name = "cardpointe_settlement_status")]
    [Description("CardPointe settlement report. date is required in YYYYMM format; it returns one result per calendar day. YYYYMMDD is also accepted for one day. Read-only.")]
    public Task<string> SettlementStatusAsync(string date) => _apiClient.SettlementStatusAsync(date);

    [McpServerTool(Name = "cardpointe_funding")]
    [Description("CardPointe funding (deposit) report. date is required in YYYYMM format; it returns one result per calendar day. YYYYMMDD is also accepted for one day. Read-only.")]
    public Task<string> FundingAsync(string date) => _apiClient.FundingAsync(date);

    [McpServerTool(Name = "cardpointe_merchant_inquiry")]
    [Description("CardPointe merchant profile info for the configured merchant. Read-only.")]
    public Task<string> MerchantInquiryAsync() => _apiClient.MerchantInquiryAsync();
}
