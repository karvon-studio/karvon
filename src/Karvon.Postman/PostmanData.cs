using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Karvon.Postman;

/// <summary>A run's data file, as Postman reads it: CSV with its names in the first row, or a JSON array of objects.</summary>
public static class PostmanData
{
    /// <summary>A file's rows are read whole.</summary>
    const long MaxSize = 50_000_000;

    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Read(string file)
    {
        if (!File.Exists(file))
            throw new PostmanException($"There is no {file} (the data file).");
        if (new FileInfo(file).Length > MaxSize)
            throw new PostmanException($"{Path.GetFileName(file)} is too large for a data file.");
        var text = File.ReadAllText(file);
        var rows = text.TrimStart('﻿', ' ', '\t', '\r', '\n').StartsWith('[') || file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? Json(file, text) : Csv(text);
        return rows.Count > 0 ? rows : throw new PostmanException($"{Path.GetFileName(file)} has no rows.");
    }

    static List<IReadOnlyDictionary<string, object?>> Json(string file, string text)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text.TrimStart('﻿'));
        }
        catch (JsonException ex)
        {
            throw new PostmanException($"{Path.GetFileName(file)} is not JSON: {ex.Message}");
        }
        if (root is not JsonArray rows || rows.Any(r => r is not JsonObject))
            throw new PostmanException($"{Path.GetFileName(file)} is not an array of objects.");
        return [.. rows.OfType<JsonObject>().Select(row => (IReadOnlyDictionary<string, object?>)row.ToDictionary(p => p.Key, p => Value(p.Value), StringComparer.Ordinal))];
    }

    static object? Value(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var text) => text,
        JsonValue v when v.TryGetValue<bool>(out var flag) => flag,
        JsonValue v when v.TryGetValue<double>(out var number) => number,
        _ => node,
    };

    /// <summary>RFC 4180's CSV: commas, or semicolons where the first row has those; quotes; a cell is text, or a number.</summary>
    internal static List<IReadOnlyDictionary<string, object?>> Csv(string text)
    {
        text = text.TrimStart('﻿');
        var first = text.Split('\n')[0];
        var comma = first.Contains(',') || !first.Contains(';') ? ',' : ';';
        var rows = new List<List<string>>();
        var (row, cell, quoted, any) = (new List<string>(), new StringBuilder(), false, false);
        void End()
        {
            row.Add(cell.ToString());
            cell.Clear();
            if (row.Count > 1 || row[0].Length > 0 || any)
                rows.Add(row);
            (row, any) = ([], false);
        }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                    cell.Append(text[++i]);
                else if (c == '"')
                    quoted = false;
                else
                    cell.Append(c);
            }
            else if (c == '"' && cell.ToString().Trim().Length == 0)
            {
                cell.Clear();
                (quoted, any) = (true, true);
            }
            else if (c == comma)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c == '\n')
                End();
            else if (c != '\r')
                cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0 || any)
            End();
        if (rows.Count == 0)
            return [];
        var names = rows[0].Select(n => n.Trim()).ToList();
        return [.. rows.Skip(1).Select(cells => (IReadOnlyDictionary<string, object?>)names.Select((name, i) => (name, Value: cells.ElementAtOrDefault(i) ?? ""))
            .Where(p => p.name.Length > 0).GroupBy(p => p.name).ToDictionary(g => g.Key, g => Cell(g.Last().Value), StringComparer.Ordinal))];
    }

    /// <summary>A number as Postman takes it ("42", "-1.5"); one that would not be written the same ("007", a long id) stays a text.</summary>
    static object Cell(string text)
    {
        var plain = text.Trim();
        return double.TryParse(plain, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
            && number.ToString(CultureInfo.InvariantCulture) == plain ? number : text;
    }
}
