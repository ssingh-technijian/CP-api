namespace CpApis.Api.OpenApi;

internal sealed class ObjectRequestBodyMetadata
{
    public ObjectRequestBodyMetadata(object requestExample, string description)
    {
        RequestExample = requestExample;
        Description = description;
    }

    public object RequestExample { get; }

    public string Description { get; }
}
