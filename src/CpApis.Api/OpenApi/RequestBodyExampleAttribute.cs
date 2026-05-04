namespace CpApis.Api.OpenApi;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
internal sealed class RequestBodyExampleAttribute : Attribute
{
    public RequestBodyExampleAttribute(string description, string exampleJson)
    {
        Description = description;
        ExampleJson = exampleJson;
    }

    public string Description { get; }
    public string ExampleJson { get; }
}
