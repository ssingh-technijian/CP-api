namespace CpApis.InventoryGenerator;

internal static class ArgumentParser
{
    public static Dictionary<string, string> Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];
            if (current.StartsWith("--", StringComparison.Ordinal) is false)
            {
                continue;
            }

            var key = current[2..];
            if (index + 1 < args.Length && args[index + 1].StartsWith("--", StringComparison.Ordinal) is false)
            {
                values[key] = args[++index];
            }
            else
            {
                values[key] = "true";
            }
        }

        return values;
    }
}
