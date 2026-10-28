using System.Text.Json;
using CpApis.Mcp.Core.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CpApis.Shared.Tests;

public sealed class EmployeeActionFileLoggerTests
{
    [Fact]
    public void PersistsOnlyApprovedMetadataWithoutFormattingSecrets()
    {
        using var folder=new OwnedFolder();var logger=new EmployeeActionFileLogger(folder.Path);var request=Guid.NewGuid();
        var fields=Fields(request);fields["Payload"]="secret-payload";fields["Sql"]="secret-sql";
        logger.Log(LogLevel.Warning,new EventId(6201),fields,new Exception("secret-exception"),(_,_)=>throw new Exception("formatter must not run"));
        var text=File.ReadAllText(Assert.Single(Directory.GetFiles(folder.Path)));using var json=JsonDocument.Parse(text);
        Assert.Equal(request,json.RootElement.GetProperty("requestId").GetGuid());Assert.Equal(1222,json.RootElement.GetProperty("sqlErrorNumber").GetInt32());
        Assert.DoesNotContain("secret-",text);Assert.Equal(8,json.RootElement.EnumerateObject().Count());
    }
    [Fact]
    public void UnrelatedEventsOrUntrustedCategoryCannotCreateFiles()
    {
        using var folder=new OwnedFolder();var logger=new EmployeeActionFileLogger(folder.Path);var fields=Fields(Guid.NewGuid());
        logger.Log(LogLevel.Information,new EventId(6201),fields,null,(_,_)=>"unused");
        logger.Log(LogLevel.Warning,new EventId(100),fields,null,(_,_)=>"unused");
        fields["Operation"]="secret-sql";logger.Log(LogLevel.Warning,new EventId(6201),fields,null,(_,_)=>"unused");
        Assert.Empty(Directory.GetFiles(folder.Path));
    }
    [Fact]
    public void RotationBoundsDailyFilesAndKeepsValidRecords()
    {
        using var folder=new OwnedFolder();var logger=new EmployeeActionFileLogger(folder.Path,512);
        for(var i=0;i<12;i++)logger.Log(LogLevel.Warning,new EventId(6201),Fields(Guid.NewGuid()),null,(_,_)=>"unused");
        var files=Directory.GetFiles(folder.Path);Assert.Equal(2,files.Length);
        foreach(var file in files){Assert.InRange(new FileInfo(file).Length,1,512);foreach(var line in File.ReadLines(file)){using var json=JsonDocument.Parse(line);Assert.Equal(6201,json.RootElement.GetProperty("eventId").GetInt32());}}
    }
    [Fact]
    public void RetentionRemovesOnlyOldOwnedLogNames()
    {
        using var folder=new OwnedFolder();var old=System.IO.Path.Combine(folder.Path,$"employee-actions-{DateTime.UtcNow.AddDays(-20):yyyyMMdd}.jsonl");
        File.WriteAllText(old,"{}");var other=System.IO.Path.Combine(folder.Path,"do-not-delete.txt");File.WriteAllText(other,"owned fixture");
        new EmployeeActionFileLogger(folder.Path).Log(LogLevel.Warning,new EventId(6201),Fields(Guid.NewGuid()),null,(_,_)=>"unused");
        Assert.False(File.Exists(old));Assert.True(File.Exists(other));
    }
    private static Dictionary<string,object?> Fields(Guid request)=>new(){["Operation"]="contract.edit",["RequestId"]=request,["ActionId"]=null,["Category"]="database",["StatusCode"]=null,["SqlErrorNumber"]=1222};
    private sealed class OwnedFolder:IDisposable
    {
        private readonly string temp=System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        public string Path{get;}
        public OwnedFolder(){Path=System.IO.Path.Combine(temp,"MCPQA_ActionLog_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path);}
        public void Dispose(){var full=System.IO.Path.GetFullPath(Path);if(System.IO.Path.GetDirectoryName(full)!=temp||!System.IO.Path.GetFileName(full).StartsWith("MCPQA_ActionLog_",StringComparison.Ordinal))throw new Exception("Cleanup boundary differs");Directory.Delete(full,true);}
    }
}
