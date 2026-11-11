using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PdfPro.Core.Operations;

namespace PdfPro.Operations.Automation;

/// <summary>What kind of value a parameter takes, for a generated form, a command line or a workflow.</summary>
public enum ParameterKind
{
    Boolean,
    Integer,
    Number,
    Text,
    Enum,

    /// <summary>A list or a record: given as JSON.</summary>
    Complex,
}

/// <summary>A parameter of an operation, read from its parameter record.</summary>
/// <param name="Name">Its name in JSON and on the command line (camelCase).</param>
/// <param name="Type">Its CLR type (nullable stripped).</param>
/// <param name="Kind">How it is entered.</param>
/// <param name="Required">It has no default and cannot be null.</param>
/// <param name="Default">Its default, or null.</param>
/// <param name="Minimum">From [Range].</param>
/// <param name="Maximum">From [Range].</param>
/// <param name="Pattern">From [RegularExpression].</param>
/// <param name="Choices">The members of an enum (camelCase, as JSON writes them).</param>
/// <param name="Secret">A password: never saved in a workflow, never shown.</param>
public sealed record ParameterField(
    string Name,
    Type Type,
    ParameterKind Kind,
    bool Required,
    object? Default,
    double? Minimum,
    double? Maximum,
    string? Pattern,
    IReadOnlyList<string> Choices,
    bool Secret);

/// <summary>
/// The parameters of an operation as fields (the primary constructor of its parameter record, with the DataAnnotations
/// of its properties): enough to generate a form, check a command line and write a workflow. Parameter records are
/// read and written as JSON the way a session manifest stores them (camelCase, enums as camelCase strings, nulls
/// left out, required constructor parameters checked).
/// </summary>
public static class ParameterSchema
{
    /// <summary>The JSON options of parameter records (the same as the session manifest's).</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>The fields of a parameter record, in constructor order.</summary>
    public static IReadOnlyList<ParameterField> Of(Type parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var constructor = parameters.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        if (constructor is null)
        {
            return [];
        }

        var nullability = new NullabilityInfoContext();
        return [.. constructor.GetParameters().Select(p => Field(parameters, p, nullability))];
    }

    /// <summary>A parameter record from JSON (null or an empty object gives its defaults), checked by its DataAnnotations.</summary>
    /// <exception cref="OperationValidationException">The values do not fit the record or break its rules.</exception>
    public static object FromJson(string operationId, Type parameters, JsonNode? json)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        object? value;
        try
        {
            value = (json ?? new JsonObject()).Deserialize(parameters, Json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException)
        {
            throw new OperationValidationException(operationId, [new OperationValidationError(null, $"The parameters cannot be read: {ex.Message}")]);
        }

        if (value is null)
        {
            throw new OperationValidationException(operationId, [new OperationValidationError(null, "The parameters are missing.")]);
        }

        Check(operationId, value);
        return value;
    }

    /// <summary>A parameter record as JSON, its secrets (passwords) left out when <paramref name="withoutSecrets"/>.</summary>
    public static JsonObject ToJson(object parameters, bool withoutSecrets = true)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var json = JsonSerializer.SerializeToNode(parameters, parameters.GetType(), Json) as JsonObject ?? [];
        if (withoutSecrets)
        {
            StripSecrets(json);
        }

        return json;
    }

    /// <summary>Takes every password out of a JSON tree (any property whose name holds "password", at any depth).</summary>
    public static void StripSecrets(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject json:
                foreach (var name in json.Select(p => p.Key).ToList())
                {
                    if (name.Contains("password", StringComparison.OrdinalIgnoreCase))
                    {
                        json.Remove(name);
                    }
                    else
                    {
                        StripSecrets(json[name]);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    StripSecrets(item);
                }

                break;
        }
    }

    /// <summary>Whether a parameter record holds a password somewhere (it must be given when the step runs).</summary>
    public static bool HasSecrets(Type parameters) => HasSecrets(parameters, 0);

    private static bool HasSecrets(Type type, int depth)
        => depth < 4 && Of(type).Any(f => f.Secret || (f.Kind == ParameterKind.Complex && !f.Type.IsArray && !f.Type.IsGenericType && f.Type.IsClass && HasSecrets(f.Type, depth + 1)));

    /// <summary>
    /// A parameter record from "name=value" pairs (a command line): booleans as true/false/yes/no/có/không/1/0, numbers in
    /// invariant form, enums by name, lists and records as JSON; names match without regard to case.
    /// </summary>
    /// <exception cref="OperationValidationException">A name is unknown, a value does not fit, or the record's rules are broken.</exception>
    public static object FromPairs(string operationId, Type parameters, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var fields = Of(parameters);
        var json = new JsonObject();
        var errors = new List<OperationValidationError>();
        foreach (var (name, text) in values)
        {
            var field = fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (field is null)
            {
                errors.Add(new OperationValidationError(name, $"'{name}' is not a parameter of {operationId}."));
                continue;
            }

            if (Node(field, text) is var (node, ok) && ok)
            {
                json[field.Name] = node;
            }
            else
            {
                errors.Add(new OperationValidationError(field.Name, $"'{text}' is not a value of {field.Name} ({field.Kind})."));
            }
        }

        if (errors.Count > 0)
        {
            throw new OperationValidationException(operationId, errors);
        }

        return FromJson(operationId, parameters, json);
    }

    /// <summary>Checks a record's DataAnnotations.</summary>
    /// <exception cref="OperationValidationException">A rule is broken.</exception>
    public static void Check(string operationId, object parameters)
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(parameters, new ValidationContext(parameters), results, validateAllProperties: true))
        {
            throw new OperationValidationException(operationId, [.. results.Select(r => new OperationValidationError(r.MemberNames.FirstOrDefault(), r.ErrorMessage ?? string.Empty))]);
        }
    }

    private static ParameterField Field(Type record, ParameterInfo parameter, NullabilityInfoContext nullability)
    {
        var name = JsonNamingPolicy.CamelCase.ConvertName(parameter.Name!);
        var property = record.GetProperty(parameter.Name!);
        var underlying = Nullable.GetUnderlyingType(parameter.ParameterType);
        var type = underlying ?? parameter.ParameterType;
        var nullable = underlying is not null || (!parameter.ParameterType.IsValueType && nullability.Create(parameter).WriteState == NullabilityState.Nullable);
        var kind = type == typeof(bool) ? ParameterKind.Boolean
            : type == typeof(int) || type == typeof(long) ? ParameterKind.Integer
            : type == typeof(double) || type == typeof(float) || type == typeof(decimal) ? ParameterKind.Number
            : type == typeof(string) ? ParameterKind.Text
            : type.IsEnum ? ParameterKind.Enum
            : ParameterKind.Complex;
        var range = property?.GetCustomAttribute<RangeAttribute>();
        var pattern = property?.GetCustomAttribute<RegularExpressionAttribute>();
        var required = !parameter.HasDefaultValue && !nullable;
        return new ParameterField(
            name,
            type,
            kind,
            required || property?.GetCustomAttribute<RequiredAttribute>() is not null && !parameter.HasDefaultValue,
            parameter.HasDefaultValue ? parameter.DefaultValue : null,
            range is null ? null : Convert.ToDouble(range.Minimum, CultureInfo.InvariantCulture),
            range is null ? null : Convert.ToDouble(range.Maximum, CultureInfo.InvariantCulture),
            pattern?.Pattern,
            type.IsEnum ? [.. Enum.GetNames(type).Select(JsonNamingPolicy.CamelCase.ConvertName)] : [],
            parameter.Name!.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    private static (JsonNode? Node, bool Ok) Node(ParameterField field, string text)
    {
        var value = text.Trim();
        switch (field.Kind)
        {
            case ParameterKind.Boolean:
                return value.ToLowerInvariant() switch
                {
                    "true" or "yes" or "1" or "có" or "co" => (JsonValue.Create(true), true),
                    "false" or "no" or "0" or "không" or "khong" => (JsonValue.Create(false), true),
                    _ => (null, false),
                };
            case ParameterKind.Integer:
                return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) ? (JsonValue.Create(whole), true) : (null, false);
            case ParameterKind.Number:
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? (JsonValue.Create(number), true) : (null, false);
            case ParameterKind.Enum:
                var choice = field.Choices.FirstOrDefault(c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase));
                return choice is null ? (null, false) : (JsonValue.Create(choice), true);
            case ParameterKind.Text:
                return (JsonValue.Create(text), true);
            default:
                try
                {
                    return (JsonNode.Parse(value), true);
                }
                catch (JsonException)
                {
                    return (null, false);
                }
        }
    }
}

/// <summary>What an operation is, for the command palette, workflows, batch runs and the command line.</summary>
/// <param name="Id">"nhom.ten".</param>
/// <param name="Group">The part before the dot ("organize", "security"…).</param>
/// <param name="Kind">Plan, content or export.</param>
/// <param name="Parameters">The parameter fields.</param>
/// <param name="ManyInputs">It works on several files at once (merge, interleave, pictures to PDF).</param>
/// <param name="WritesPdf">It writes one PDF from one (a workflow step can follow it).</param>
/// <param name="Measured">Its parameters are measured on each file (blank pages, tilt…): a workflow step needs none.</param>
public sealed record OperationDescriptor(string Id, string Group, OperationKind Kind, Type ParamsType, IReadOnlyList<ParameterField> Parameters, bool ManyInputs, bool WritesPdf, bool Measured);

/// <summary>Descriptors of the operations of a catalog.</summary>
public static class OperationDescriptors
{
    /// <summary>Operations that take several input files.</summary>
    public static IReadOnlySet<string> ManyInputs { get; } = new HashSet<string>(StringComparer.Ordinal) { "organize.merge", "organize.interleave", "convert.fromImages", "convert.officeToPdf" };

    /// <summary>Operations whose output is not one PDF (pictures, text, tables, several files).</summary>
    public static IReadOnlySet<string> OtherOutputs { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "convert.toImages", "convert.toText", "extract.images", "extract.tables", "organize.split", "organize.extract", "forms.merge", "stamp.bates",
    };

    /// <summary>Operations whose parameters are measured on each file in a workflow.</summary>
    public static IReadOnlySet<string> MeasuredOperations { get; } = new HashSet<string>(StringComparer.Ordinal) { "scan.removeBlankPages", "scan.autoRotate", "scan.deskew", "scan.cleanBorders" };

    public static OperationDescriptor Describe(IPdfOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var group = operation.Id.Split('.')[0];
        return new OperationDescriptor(
            operation.Id,
            group,
            operation.Kind,
            operation.ParamsType,
            ParameterSchema.Of(operation.ParamsType),
            ManyInputs.Contains(operation.Id),
            !ManyInputs.Contains(operation.Id) && !OtherOutputs.Contains(operation.Id),
            MeasuredOperations.Contains(operation.Id));
    }
}
