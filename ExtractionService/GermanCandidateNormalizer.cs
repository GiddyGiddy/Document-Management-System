using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ExtractionService;

/// <summary>
/// Repairs locale-specific formatting that models commonly leave in extracted JSON for German documents
/// (DD.MM.YYYY dates, 1.234,56 numbers, currency symbols, German property names) so strict validation
/// checks the extracted content rather than its formatting.
/// </summary>
public static class GermanCandidateNormalizer
{
    private static readonly string[] DateFormats =
        ["yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy", "dd/MM/yyyy", "d/M/yyyy"];

    private static readonly HashSet<string> DateProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "invoiceDate", "dueDate", "effectiveDate", "expirationDate", "issueDate", "expiryDate",
        "dateOfBirth", "periodStart", "periodEnd"
    };

    private static readonly HashSet<string> NumberProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "subtotal", "tax", "total", "quantity", "unitPrice", "lineTotal",
        "revenue", "expenses", "netIncome", "assets", "liabilities", "equity"
    };

    private static readonly Dictionary<string, string> CurrencyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["€"] = "EUR", ["euro"] = "EUR", ["eur"] = "EUR",
        ["$"] = "USD", ["us-dollar"] = "USD", ["dollar"] = "USD",
        ["£"] = "GBP", ["pfund"] = "GBP",
        ["fr."] = "CHF", ["sfr"] = "CHF", ["franken"] = "CHF", ["schweizer franken"] = "CHF"
    };

    private static readonly Dictionary<string, string> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lieferant"] = "vendorName", ["rechnungssteller"] = "vendorName", ["verkäufer"] = "vendorName",
        ["rechnungsnummer"] = "invoiceNumber", ["rechnungsdatum"] = "invoiceDate",
        ["fälligkeitsdatum"] = "dueDate", ["zahlungsziel"] = "dueDate", ["währung"] = "currency",
        ["positionen"] = "lineItems", ["rechnungspositionen"] = "lineItems",
        ["zwischensumme"] = "subtotal", ["nettobetrag"] = "subtotal", ["netto"] = "subtotal",
        ["steuer"] = "tax", ["mwst"] = "tax", ["umsatzsteuer"] = "tax",
        ["gesamtbetrag"] = "total", ["bruttobetrag"] = "total", ["gesamtsumme"] = "total",
        ["beschreibung"] = "description", ["bezeichnung"] = "description", ["menge"] = "quantity",
        ["einzelpreis"] = "unitPrice", ["gesamtpreis"] = "lineTotal", ["positionssumme"] = "lineTotal",
        ["vertragsart"] = "contractType", ["vertragsparteien"] = "parties", ["parteien"] = "parties",
        ["vertragsbeginn"] = "effectiveDate", ["vertragsende"] = "expirationDate",
        ["anwendbaresrecht"] = "governingLaw", ["kündigungsklausel"] = "terminationClause",
        ["dokumenttyp"] = "documentType", ["vollständigername"] = "fullName",
        ["dokumentnummer"] = "documentNumber", ["ausstellungsland"] = "issuingCountry",
        ["ausstellungsdatum"] = "issueDate", ["ablaufdatum"] = "expiryDate", ["geburtsdatum"] = "dateOfBirth",
        ["unternehmen"] = "entityName", ["berichtstyp"] = "statementType",
        ["periodenbeginn"] = "periodStart", ["periodenende"] = "periodEnd",
        ["umsatz"] = "revenue", ["aufwendungen"] = "expenses", ["jahresüberschuss"] = "netIncome",
        ["vermögen"] = "assets", ["verbindlichkeiten"] = "liabilities", ["eigenkapital"] = "equity"
    };

    private static readonly Regex ThinkBlock = new("<think>.*?</think>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ThousandsOnly = new(@"^-?\d{1,3}(\.\d{3})+$", RegexOptions.Compiled);
    private static readonly Regex NonNumeric = new(@"[^0-9,.\-]", RegexOptions.Compiled);

    public static string Normalize(string kind, string candidateJson)
    {
        var text = StripNoise(candidateJson);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return text;
        }

        if (root is not JsonObject obj || !SupportedDocumentKinds.Structured.Contains(kind))
        {
            return text;
        }

        var before = obj.ToJsonString();
        NormalizeObject(obj);
        if (obj["lineItems"] is JsonArray items)
        {
            foreach (var item in items.OfType<JsonObject>())
            {
                NormalizeObject(item);
            }
        }

        var after = obj.ToJsonString();
        return after == before ? text : after;
    }

    private static void NormalizeObject(JsonObject obj)
    {
        foreach (var property in obj.ToList())
        {
            var key = property.Key.Replace(" ", string.Empty).Replace("_", string.Empty);
            if (KeyAliases.TryGetValue(key, out var english) && !obj.ContainsKey(english))
            {
                obj.Remove(property.Key);
                obj[english] = property.Value;
            }
        }

        foreach (var property in obj.ToList())
        {
            if (property.Value is not JsonValue value || !value.TryGetValue<string>(out var raw))
            {
                continue;
            }

            if (DateProperties.Contains(property.Key))
            {
                obj[property.Key] = TryParseDate(raw, out var date) ? date : string.IsNullOrWhiteSpace(raw) ? null : JsonValue.Create(raw);
            }
            else if (NumberProperties.Contains(property.Key))
            {
                obj[property.Key] = TryParseNumber(raw, out var number) ? JsonValue.Create(number) : JsonValue.Create(raw);
            }
            else if (property.Key.Equals("currency", StringComparison.OrdinalIgnoreCase))
            {
                obj[property.Key] = JsonValue.Create(NormalizeCurrency(raw));
            }
        }
    }

    public static string StripNoise(string text)
    {
        text = ThinkBlock.Replace(text, string.Empty).Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var start = text.IndexOf('\n');
            var end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (start >= 0 && end > start)
            {
                text = text[(start + 1)..end].Trim();
            }
        }

        return text;
    }

    public static bool TryParseDate(string raw, out JsonNode? iso)
    {
        iso = null;
        var cleaned = raw.Trim().TrimEnd('.');
        if (DateOnly.TryParseExact(cleaned, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            iso = JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            return true;
        }

        return false;
    }

    public static bool TryParseNumber(string raw, out decimal number)
    {
        number = 0;
        var text = raw.Trim().Replace('\u2212', '-');
        var negative = text.StartsWith('(') && text.EndsWith(')') || text.EndsWith('-');
        text = NonNumeric.Replace(text, string.Empty).Trim('-');
        if (text.Length == 0)
        {
            return false;
        }

        negative |= raw.TrimStart().StartsWith('-') || raw.TrimStart().StartsWith('\u2212');
        var lastComma = text.LastIndexOf(',');
        var lastDot = text.LastIndexOf('.');
        if (lastComma >= 0 && lastDot >= 0)
        {
            text = lastComma > lastDot
                ? text.Replace(".", string.Empty).Replace(',', '.')
                : text.Replace(",", string.Empty);
        }
        else if (lastComma >= 0)
        {
            text = text.Count(c => c == ',') > 1 ? text.Replace(",", string.Empty) : text.Replace(',', '.');
        }
        else if (lastDot >= 0 && ThousandsOnly.IsMatch(text))
        {
            text = text.Replace(".", string.Empty);
        }

        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number))
        {
            return false;
        }

        if (negative)
        {
            number = -number;
        }

        return true;
    }

    public static string NormalizeCurrency(string raw)
    {
        var trimmed = raw.Trim();
        return CurrencyAliases.TryGetValue(trimmed, out var iso) ? iso : trimmed.ToUpperInvariant();
    }
}
