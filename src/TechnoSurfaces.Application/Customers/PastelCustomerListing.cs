using System.Text;

namespace TechnoSurfaces.Application.Customers;

/// <summary>One customer as Sage Pastel prints it on its Customer Listing report.</summary>
public sealed record PastelCustomerRecord(
    string AccountCode,
    string Name,
    IReadOnlyList<string> DeliveryAddress,
    IReadOnlyList<string> PostalAddress,
    string? Contact,
    string? Telephone,
    string? MobilePhone,
    string? Email,
    string? TaxReference,
    bool Blocked);

/// <summary>The customers read from a listing, and anything in it that could not be read.</summary>
public sealed record PastelListing(IReadOnlyList<PastelCustomerRecord> Customers, IReadOnlyList<string> Problems);

/// <summary>
/// Reads the Customer Listing report that Sage Pastel exports as text. Each customer
/// is a block of comma-separated lines that starts with an "Acc" line. The first two
/// columns hold the account code, the name and then the delivery and postal addresses
/// line by line; the other four are label and value pairs such as "Email:" and the
/// address. Lines before the first block (the report heading) are ignored.
/// </summary>
public static class PastelCustomerListing
{
    /// <summary>
    /// Decodes the uploaded file. Pastel writes Windows text; anything that is not
    /// valid UTF-8 is read as Latin-1, so an accented name is kept rather than refused.
    /// </summary>
    public static async Task<PastelListing> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes);
        }

        return Parse(text.TrimStart('﻿'));
    }

    public static PastelListing Parse(string text)
    {
        var blocks = new List<List<string[]>>();
        List<string[]>? block = null;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            var fields = SplitLine(trimmed);
            if (fields[0] == "Acc")
            {
                block = new List<string[]>();
                blocks.Add(block);
            }

            block?.Add(fields);
        }

        var customers = new List<PastelCustomerRecord>();
        var problems = new List<string>();
        foreach (var b in blocks)
        {
            if (Read(b) is { } record)
                customers.Add(record);
            else
                problems.Add($"A customer block starting \"{string.Join(",", b[0].Take(2))}\" has no account code, so it was not read.");
        }

        if (blocks.Count == 0)
            problems.Add("No customers were found. Export the Customer Listing report from Sage Pastel as a text file.");

        return new PastelListing(customers, problems);
    }

    private static PastelCustomerRecord? Read(List<string[]> block)
    {
        var code = Clean(Field(block[0], 1));
        if (code is null)
            return null;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? name = null;
        var delivery = new List<string>();
        var postal = new List<string>();

        foreach (var row in block)
        {
            AddPair(values, Field(row, 2), Field(row, 3));
            AddPair(values, Field(row, 4), Field(row, 5));

            switch (row[0])
            {
                case "Acc":
                case "Delivery Address":
                    break;
                case "Name":
                    name = Clean(Field(row, 1));
                    break;
                default:
                    if (Clean(row[0]) is { } d) delivery.Add(d);
                    if (Clean(Field(row, 1)) is { } p) postal.Add(p);
                    break;
            }
        }

        return new PastelCustomerRecord(
            code,
            name ?? "",
            delivery,
            postal,
            Value(values, "Contact"),
            Value(values, "Telephone"),
            Value(values, "Mobile Phone"),
            Value(values, "Email"),
            Value(values, "Tax Reference"),
            string.Equals(Value(values, "Blocked"), "Yes", StringComparison.OrdinalIgnoreCase));
    }

    private static void AddPair(Dictionary<string, string> values, string label, string value)
    {
        var key = label.Trim().TrimEnd(':');
        if (key.Length > 0)
            values.TryAdd(key, value);
    }

    private static string? Value(Dictionary<string, string> values, string label) =>
        values.TryGetValue(label, out var v) ? Clean(v) : null;

    private static string Field(string[] row, int index) => index < row.Length ? row[index] : "";

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Splits one line of the report. A field is either quoted, with "" standing for a
    /// quote inside it, or bare; Pastel leaves some empty fields bare (",,").
    /// </summary>
    internal static string[] SplitLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
