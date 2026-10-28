using CpApis.Shared.Inventory;
using CpApis.Shared.Services;
using Xunit;

namespace CpApis.Shared.Tests;

/// <summary>
/// Regression guard for the 2026-09-15 finding: credential-returning stored procedures
/// (stp_xml_LookupV_Cred_Get and friends) were reachable through the public REST API and the MCP
/// execute_stored_procedure tool. The deny list must strip them at manifest load time.
/// </summary>
public sealed class ManifestCatalogServiceDenyListTests
{
    // Exact names published by the live API on 2026-09-15 (swagger.json), plus the views behind them.
    private static readonly string[] MustBeDenied =
    [
        "stp_xml_LookupV_Cred_Get",
        "stp_xml_EWS_Cred_Get",
        "stp_xml_LookupV_QBCred_Get",
        "stp_xml_LookupV_MXToolCred_Get",
        "stp_xml_LookupV_NumVerifyCred_Get",
        "stp_xml_LookupV_PRTGRunner_Cred_Get",
        "stp_xml_Docusign_Cred_Get",
        "stp_xml_UsersProfile_ProfileCredentials_List_Get",
        "stp_xml_UsersProfile_ProfileCredentials_Save",
        "stp_xml_APIKey_Get",
        "vw_LookupV_Cred",
        "vw_LookupV_EWSCred",
        "vw_LookupV_MXToolCred",
        "vw_LookupV_PRTGCred",
        "vw_LookupV_QBCred",
        "fn_dt_UsersProfile_ProfileCredentials_List_Get"
    ];

    // Legitimate objects that must survive, including the "Credit" near-miss.
    private static readonly string[] MustBeKept =
    [
        "stp_xml_Profile_List_Get",
        "stp_xml_Authorization_Save",
        "stp_SettlementStatus_Save",
        "stp_xml_LookupV_List_Get",
        "stp_Inv_Credit_Memo_Get",
        "vw_UnusedCredits"
    ];

    [Fact]
    public void BuiltInPatterns_DenyEveryKnownCredentialObject_AndKeepEverythingElse()
    {
        var raw = BuildManifest(MustBeDenied.Concat(MustBeKept));

        var (exposed, deniedKeys) = ManifestCatalogService.ApplyDenyList(raw, additionalPatterns: null);

        var exposedNames = exposed.Objects.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in MustBeDenied)
        {
            Assert.DoesNotContain(name, exposedNames);
            Assert.Contains(KeyFor(name), deniedKeys, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var name in MustBeKept)
        {
            Assert.Contains(name, exposedNames);
        }

        Assert.Equal(MustBeDenied.Length, deniedKeys.Count);
        Assert.Equal(MustBeKept.Length, exposed.Objects.Count);
    }

    [Fact]
    public void AdditionalPattern_AddsToBuiltIns_ButCannotRemoveThem()
    {
        var raw = BuildManifest(["stp_xml_LookupV_Cred_Get", "stp_Change_Password", "stp_xml_Profile_List_Get"]);

        // An additional pattern that only matches the password procedure. Passing a pattern can never
        // re-enable a built-in denial because the sets are OR-ed together.
        var (exposed, deniedKeys) = ManifestCatalogService.ApplyDenyList(raw, ["^stp_Change_Password$"]);

        Assert.Equal(["stp_xml_Profile_List_Get"], exposed.Objects.Select(item => item.Name).ToArray());
        Assert.Equal(2, deniedKeys.Count);
    }

    [Fact]
    public void InvalidAdditionalPattern_FailsClosedAtLoad()
    {
        var raw = BuildManifest(["stp_xml_Profile_List_Get"]);

        var ex = Assert.Throws<ArgumentException>(() => ManifestCatalogService.ApplyDenyList(raw, ["("]));

        Assert.Contains("denied-name pattern", ex.Message);
    }

    [Fact]
    public void Matching_IsCaseInsensitive()
    {
        var raw = BuildManifest(["STP_XML_LOOKUPV_CRED_GET", "stp_xml_apikey_get", "Stp_Get_Secret_Thing"]);

        var (exposed, _) = ManifestCatalogService.ApplyDenyList(raw, null);

        Assert.Empty(exposed.Objects);
    }

    [Fact]
    public void DatabasesAndHeaderFields_ArePreservedUnchanged()
    {
        var raw = BuildManifest(["stp_xml_Profile_List_Get"]);

        var (exposed, _) = ManifestCatalogService.ApplyDenyList(raw, null);

        Assert.Same(raw.Databases, exposed.Databases);
        Assert.Equal(raw.GeneratedAtUtc, exposed.GeneratedAtUtc);
        Assert.Equal(raw.SourceRoot, exposed.SourceRoot);
    }

    private static DatabaseInventoryManifest BuildManifest(IEnumerable<string> names) => new()
    {
        GeneratedAtUtc = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero),
        SourceRoot = @"D:\Technijian",
        Databases = [new DatabaseTargetManifest { Alias = "client-portal", DatabaseName = "CP_Production_New" }],
        Objects = names.Select(name => new DatabaseObjectManifest
        {
            Key = KeyFor(name),
            Module = "dbo",
            DatabaseAlias = "client-portal",
            DatabaseName = "CP_Production_New",
            Schema = "dbo",
            Name = name,
            Kind = name.StartsWith("vw_", StringComparison.OrdinalIgnoreCase) ? "view" : "stored-procedure",
            IsActiveRoot = true,
            IsIncluded = true
        }).ToList()
    };

    private static string KeyFor(string name) =>
        $"client-portal:dbo.{name.ToLowerInvariant()}:{(name.StartsWith("vw_", StringComparison.OrdinalIgnoreCase) ? "view" : "stored-procedure")}";
}
