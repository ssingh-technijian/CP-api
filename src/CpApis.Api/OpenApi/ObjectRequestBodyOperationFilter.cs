using System.Collections;
using System.Text.Json;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace CpApis.Api.OpenApi;

internal sealed class ObjectRequestBodyOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<ObjectRequestBodyMetadata>()
            .FirstOrDefault();

        if (metadata is null || operation.RequestBody?.Content is null)
        {
            return;
        }

        var example = OpenApiAnyBuilder.FromObject(metadata.RequestExample);
        operation.RequestBody.Description = metadata.Description;

        foreach (var mediaType in operation.RequestBody.Content.Values)
        {
            mediaType.Example = example;
            mediaType.Schema.Example = example;
        }
    }

    private static class OpenApiAnyBuilder
    {
        public static IOpenApiAny FromObject(object? value)
        {
            if (value is null)
            {
                return new OpenApiNull();
            }

            return value switch
            {
                JsonElement jsonElement => FromJsonElement(jsonElement),
                string stringValue => new OpenApiString(stringValue),
                bool boolValue => new OpenApiBoolean(boolValue),
                byte byteValue => new OpenApiInteger(byteValue),
                short shortValue => new OpenApiInteger(shortValue),
                int intValue => new OpenApiInteger(intValue),
                long longValue => new OpenApiLong(longValue),
                float floatValue => new OpenApiDouble(floatValue),
                double doubleValue => new OpenApiDouble(doubleValue),
                decimal decimalValue => new OpenApiDouble(Convert.ToDouble(decimalValue)),
                IDictionary dictionary => FromDictionary(dictionary),
                IEnumerable enumerable when value is not string => FromEnumerable(enumerable),
                _ => FromJsonElement(JsonSerializer.SerializeToElement(value))
            };
        }

        private static IOpenApiAny FromJsonElement(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Object => FromObjectElement(element),
                JsonValueKind.Array => FromArrayElement(element),
                JsonValueKind.String => new OpenApiString(element.GetString() ?? string.Empty),
                JsonValueKind.Number => element.TryGetInt32(out var intValue)
                    ? new OpenApiInteger(intValue)
                    : element.TryGetInt64(out var longValue)
                        ? new OpenApiLong(longValue)
                        : new OpenApiDouble(element.GetDouble()),
                JsonValueKind.True => new OpenApiBoolean(true),
                JsonValueKind.False => new OpenApiBoolean(false),
                JsonValueKind.Null or JsonValueKind.Undefined => new OpenApiNull(),
                _ => new OpenApiString(element.ToString())
            };
        }

        private static OpenApiObject FromObjectElement(JsonElement element)
        {
            var openApiObject = new OpenApiObject();
            foreach (var property in element.EnumerateObject())
            {
                openApiObject[property.Name] = FromJsonElement(property.Value);
            }

            return openApiObject;
        }

        private static OpenApiArray FromArrayElement(JsonElement element)
        {
            var array = new OpenApiArray();
            foreach (var item in element.EnumerateArray())
            {
                array.Add(FromJsonElement(item));
            }

            return array;
        }

        private static OpenApiObject FromDictionary(IDictionary dictionary)
        {
            var openApiObject = new OpenApiObject();
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is null)
                {
                    continue;
                }

                openApiObject[entry.Key.ToString() ?? string.Empty] = FromObject(entry.Value);
            }

            return openApiObject;
        }

        private static OpenApiArray FromEnumerable(IEnumerable enumerable)
        {
            var array = new OpenApiArray();
            foreach (var item in enumerable)
            {
                array.Add(FromObject(item));
            }

            return array;
        }
    }
}
