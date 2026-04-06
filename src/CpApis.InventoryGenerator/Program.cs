using CpApis.InventoryGenerator;

var arguments = ArgumentParser.Parse(args);
var repoRoot = Path.GetFullPath(arguments.TryGetValue("repo-root", out var repoRootValue) ? repoRootValue : @"D:\Technijian");
var outputDirectory = Path.GetFullPath(arguments.TryGetValue("output-dir", out var outputDirectoryValue)
    ? outputDirectoryValue
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts"));
arguments.TryGetValue("client-portal-connection-string", out var clientPortalConnectionString);
arguments.TryGetValue("teams-connection-string", out var teamsConnectionString);
arguments.TryGetValue("client-portal-teams-connection-string", out var clientPortalTeamsConnectionString);

Directory.CreateDirectory(outputDirectory);

var builder = new InventoryBuilder(
    repoRoot,
    outputDirectory,
    clientPortalConnectionString,
    teamsConnectionString,
    clientPortalTeamsConnectionString);
await builder.RunAsync();
