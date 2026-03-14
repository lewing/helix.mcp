using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HelixTool.Core.CliSchema;

public static class SchemaGenerator
{
    private const int MaxDepth = 5;
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string GenerateSchema<T>() => GenerateSchema(typeof(T));

    public static string GenerateSchema(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var schema = CreateSchemaValue(type, new HashSet<Type>(), 0);
        return JsonSerializer.Serialize(schema, s_jsonOptions);
    }

    private static object? CreateSchemaValue(Type type, HashSet<Type> visited, int depth)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (TryGetSimplePlaceholder(type, out var placeholder))
            return placeholder;

        if (depth >= MaxDepth)
            return "<circular>";

        if (TryGetDictionaryValueType(type, out var valueType))
        {
            return new Dictionary<string, object?>
            {
                ["<key>"] = CreateSchemaValue(valueType, visited, depth + 1)
            };
        }

        var elementType = GetEnumerableElementType(type);
        if (elementType is not null)
            return new[] { CreateSchemaValue(elementType, visited, depth + 1) };

        if (!visited.Add(type))
            return "<circular>";

        try
        {
            var properties = type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(static property => property.CanRead && property.GetIndexParameters().Length == 0)
                .Where(static property => property.GetCustomAttribute<JsonIgnoreAttribute>() is null)
                .OrderBy(property => property.MetadataToken);

            var schema = new Dictionary<string, object?>();
            foreach (var property in properties)
            {
                var jsonName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
                schema[jsonName] = CreateSchemaValue(property.PropertyType, visited, depth + 1);
            }

            return schema;
        }
        finally
        {
            visited.Remove(type);
        }
    }

    private static bool TryGetSimplePlaceholder(Type type, out object? placeholder)
    {
        if (type.IsEnum)
        {
            placeholder = $"<{string.Join('|', Enum.GetNames(type))}>";
            return true;
        }

        placeholder = Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => false,
            TypeCode.Byte => 0,
            TypeCode.SByte => 0,
            TypeCode.Int16 => 0,
            TypeCode.UInt16 => 0,
            TypeCode.Int32 => 0,
            TypeCode.UInt32 => 0,
            TypeCode.Int64 => 0,
            TypeCode.UInt64 => 0,
            TypeCode.Single => 0,
            TypeCode.Double => 0,
            TypeCode.Decimal => 0,
            TypeCode.String => "<string>",
            TypeCode.DateTime => "<datetime>",
            _ => null
        };

        if (placeholder is not null)
            return true;

        if (type == typeof(Guid))
        {
            placeholder = "<guid>";
            return true;
        }

        if (type == typeof(DateTimeOffset))
        {
            placeholder = "<datetime>";
            return true;
        }

        if (type == typeof(TimeSpan))
        {
            placeholder = "<string>";
            return true;
        }

        if (type == typeof(Uri))
        {
            placeholder = "<string>";
            return true;
        }

        return false;
    }

    private static bool TryGetDictionaryValueType(Type type, out Type valueType)
    {
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Dictionary<,>) ||
                definition == typeof(IDictionary<,>) ||
                definition == typeof(IReadOnlyDictionary<,>))
            {
                valueType = type.GetGenericArguments()[1];
                return true;
            }
        }

        var dictionaryInterface = type.GetInterfaces()
            .FirstOrDefault(static iface => iface.IsGenericType &&
                (iface.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                 iface.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

        if (dictionaryInterface is not null)
        {
            valueType = dictionaryInterface.GetGenericArguments()[1];
            return true;
        }

        valueType = null!;
        return false;
    }

    private static Type? GetEnumerableElementType(Type type)
    {
        if (type == typeof(string))
            return null;

        if (type.IsArray)
            return type.GetElementType();

        if (!typeof(IEnumerable).IsAssignableFrom(type))
            return null;

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return type.GetGenericArguments()[0];

        return type.GetInterfaces()
            .FirstOrDefault(static iface => iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }
}
