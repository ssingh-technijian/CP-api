using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CpApis.Mcp.Core.Tools;
using Microsoft.Extensions.Logging;

namespace CpApis.Mcp.Core.Services;

/// <summary>Private bounded action diagnostics. Never formats payloads, SQL or exceptions.</summary>
public sealed class EmployeeActionFileLogger(string directory,int maxBytes=5*1024*1024):ILogger<ClientPortalEmployeeActionTools>
{
    private static readonly object Gate=new();
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly string root=Path.GetFullPath(directory);
    private readonly int limit=maxBytes is >=512 and <=5*1024*1024?maxBytes:throw new ArgumentOutOfRangeException(nameof(maxBytes));
    public IDisposable? BeginScope<TState>(TState state)where TState:notnull=>null;
    public bool IsEnabled(LogLevel level)=>level>=LogLevel.Warning&&level!=LogLevel.None;
    public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> formatter)
    {
        if(!IsEnabled(level)||id.Id!=6201||state is not IEnumerable<KeyValuePair<string,object?>> values)return;
        var fields=values.ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
        var operation=fields.GetValueOrDefault("Operation") as string;
        if(operation is null||!Regex.IsMatch(operation,@"^(?:(?:ticket|time|invoice|contract)\.(?:create|edit|delete)|status)$",RegexOptions.CultureInvariant))return;
        var category=fields.GetValueOrDefault("Category") as string;
        if(category is not ("database" or "action-rule" or "permission"))return;
        var now=DateTime.UtcNow;
        var row=new {timestampUtc=now,eventId=6201,operation,requestId=GuidValue("RequestId"),actionId=GuidValue("ActionId"),category,
            statusCode=IntValue("StatusCode"),sqlErrorNumber=IntValue("SqlErrorNumber")};
        var text=JsonSerializer.Serialize(row,Json)+Environment.NewLine;
        lock(Gate)
        {
            Directory.CreateDirectory(root);
            var path=Path.Combine(root,$"employee-actions-{now:yyyyMMdd}.jsonl");
            if(File.Exists(path)&&new FileInfo(path).Length+Encoding.UTF8.GetByteCount(text)>limit)File.Move(path,path+".previous",true);
            File.AppendAllText(path,text,new UTF8Encoding(false));
            foreach(var old in Directory.EnumerateFiles(root,"employee-actions-*.jsonl*"))
            {
                var match=Regex.Match(Path.GetFileName(old),@"^employee-actions-(\d{8})\.jsonl(?:\.previous)?$",RegexOptions.CultureInvariant);
                if(match.Success&&DateTime.TryParseExact(match.Groups[1].Value,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var day)
                    &&day<now.Date.AddDays(-14))File.Delete(old);
            }
        }
        Guid? GuidValue(string key)=>fields.GetValueOrDefault(key) is Guid value?value:null;
        int? IntValue(string key)=>fields.GetValueOrDefault(key) is int value?value:null;
    }
}
