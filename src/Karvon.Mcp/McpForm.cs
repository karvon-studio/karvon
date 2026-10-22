using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using ModelContextProtocol.Protocol;

namespace Karvon.Mcp;

public enum McpFieldKind
{
    /// <summary>A string or a number, typed.</summary>
    Text,

    /// <summary>One of the schema's enum values.</summary>
    Choice,

    /// <summary>A boolean: a check box (three states when it may be left out).</summary>
    Flag,

    /// <summary>An object, an array or anything: JSON, typed.</summary>
    Json,
}

/// <summary>An enum value: its label, and the JSON it is sent as (null for "not set").</summary>
public sealed record McpChoice(string Label, JsonElement? Value)
{
    public override string ToString() => Label;
}

/// <summary>One argument of a tool or a prompt: its name, type and description, and what the user gave.</summary>
public sealed partial class McpField(string name, string type, string? description, bool isRequired, McpFieldKind kind) : ObservableObject
{
    public string Name { get; } = name;

    /// <summary>The schema's type: string, integer, number, boolean, object, array, or "any".</summary>
    public string Type { get; } = type;

    public string? Description { get; } = description;

    public bool HasDescription => Description is { Length: > 0 };

    public bool IsRequired { get; } = isRequired;

    public McpFieldKind Kind { get; } = kind;

    public bool IsText => Kind == McpFieldKind.Text;

    public bool IsChoice => Kind == McpFieldKind.Choice;

    public bool IsFlag => Kind == McpFieldKind.Flag;

    public bool IsJson => Kind == McpFieldKind.Json;

    public IReadOnlyList<McpChoice> Choices { get; init; } = [];

    /// <summary>The schema's default ("default: 10"), or what JSON goes there; never sent for the user.</summary>
    public string? Placeholder { get; init; }

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial McpChoice? Choice { get; set; }

    /// <summary>A check box's state; null is "not set" (an optional boolean is then left out).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlagText))]
    public partial bool? Flag { get; set; }

    public string FlagText => Flag switch { true => "true", false => "false", _ => "(not set)" };
}

/// <summary>
/// A form for a tool's arguments, from its input schema (the top-level properties), or for a prompt's: typed text for
/// strings and numbers, a list for an enum, a check box for a boolean, JSON for objects and arrays. Its values are
/// what the call sends: what was left empty is left out, a required argument left empty is an error.
/// </summary>
public sealed class McpForm
{
    McpForm(IReadOnlyList<McpField> fields) => Fields = fields;

    public IReadOnlyList<McpField> Fields { get; }

    public bool IsEmpty => Fields.Count == 0;

    public static McpForm ForTool(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return new McpForm([]);
        var required = schema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
        return new McpForm([.. properties.EnumerateObject().Select(p => Field(p.Name, p.Value, required.Contains(p.Name)))]);
    }

    public static McpForm ForPrompt(IEnumerable<PromptArgument>? arguments) =>
        new([.. (arguments ?? []).Select(a => new McpField(a.Name, "string", a.Description, a.Required == true, McpFieldKind.Text))]);

    static McpField Field(string name, JsonElement property, bool required)
    {
        var type = TypeOf(property);
        var description = property.ValueKind == JsonValueKind.Object && property.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        var placeholder = property.ValueKind == JsonValueKind.Object && property.TryGetProperty("default", out var def) ? "default: " + Shown(def)
            : type == "object" ? "{ }  JSON" : type == "array" ? "[ ]  JSON" : null;
        if (property.ValueKind == JsonValueKind.Object && property.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            var choices = values.EnumerateArray().Select(v => new McpChoice(Shown(v), v.Clone())).ToList();
            if (!required)
                choices.Insert(0, new McpChoice("(not set)", null));
            return new McpField(name, type, description, required, McpFieldKind.Choice) { Choices = choices, Placeholder = placeholder, Choice = choices.FirstOrDefault() };
        }
        return type switch
        {
            "boolean" => new McpField(name, type, description, required, McpFieldKind.Flag) { Placeholder = placeholder, Flag = required ? false : null },
            "object" or "array" or "any" => new McpField(name, type, description, required, McpFieldKind.Json) { Placeholder = placeholder },
            _ => new McpField(name, type, description, required, McpFieldKind.Text) { Placeholder = placeholder },
        };
    }

    /// <summary>The type: the first that is not null of a list, of anyOf/oneOf; guessed from enum, properties or items.</summary>
    static string TypeOf(JsonElement property)
    {
        if (property.ValueKind != JsonValueKind.Object)
            return "any";
        if (property.TryGetProperty("type", out var type))
        {
            if (type.ValueKind == JsonValueKind.String)
                return type.GetString()!;
            if (type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Select(t => t.GetString()).FirstOrDefault(t => t is not null and not "null") is { } first)
                return first;
        }
        foreach (var union in new[] { "anyOf", "oneOf" })
            if (property.TryGetProperty(union, out var options) && options.ValueKind == JsonValueKind.Array
                && options.EnumerateArray().Select(TypeOf).FirstOrDefault(t => t is not "null" and not "any") is { } chosen)
                return chosen;
        if (property.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array && values.EnumerateArray().FirstOrDefault() is { ValueKind: not JsonValueKind.Undefined } value)
            return value.ValueKind switch { JsonValueKind.String => "string", JsonValueKind.Number => "number", JsonValueKind.True or JsonValueKind.False => "boolean", _ => "any" };
        return property.TryGetProperty("properties", out _) ? "object" : property.TryGetProperty("items", out _) ? "array" : "any";
    }

    static string Shown(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    /// <summary>The arguments to send; a <see cref="FormatException"/> says which one is wrong.</summary>
    public Dictionary<string, object?> Values()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            var value = ValueOf(field);
            if (value is not null)
                values[field.Name] = value;
            else if (field.IsRequired)
                throw new FormatException($"{field.Name} is required.");
        }
        return values;
    }

    static object? ValueOf(McpField field)
    {
        switch (field.Kind)
        {
            case McpFieldKind.Choice:
                return field.Choice?.Value;
            case McpFieldKind.Flag:
                return field.Flag;
        }
        var text = field.Text.Trim();
        if (text.Length == 0)
            return null;
        switch (field.Type)
        {
            case "integer":
                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) ? integer
                    : throw new FormatException($"{field.Name} is not an integer.");
            case "number":
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number
                    : throw new FormatException($"{field.Name} is not a number.");
            case "string":
                return field.Text;
        }
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // "any" also takes plain words.
            return field.Kind == McpFieldKind.Json && field.Type != "any" ? throw new FormatException($"{field.Name} is not valid JSON.") : field.Text;
        }
    }

    /// <summary>For tests and scripts: the text of a field, or a choice by its label, or a check box ("true", "false", "").</summary>
    public void Set(string name, string text)
    {
        var field = Fields.First(f => f.Name == name);
        switch (field.Kind)
        {
            case McpFieldKind.Choice:
                field.Choice = field.Choices.First(c => c.Label == text);
                break;
            case McpFieldKind.Flag:
                field.Flag = text.Length == 0 ? null : bool.Parse(text);
                break;
            default:
                field.Text = text;
                break;
        }
    }
}
