using System.Text.RegularExpressions;

namespace ExtractionService;

public sealed class InvoiceValidator
{
    private static readonly Regex CurrencyPattern = new("^[A-Z]{3}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public IReadOnlyList<ExtractionValidationIssue> Validate(InvoiceExtraction invoice, decimal roundingTolerance = 0.01m)
    {
        var issues = new List<ExtractionValidationIssue>();
        if (string.IsNullOrWhiteSpace(invoice.VendorName))
        {
            issues.Add(new ExtractionValidationIssue("vendorName", "Vendor name is required."));
        }

        if (string.IsNullOrWhiteSpace(invoice.InvoiceNumber))
        {
            issues.Add(new ExtractionValidationIssue("invoiceNumber", "Invoice number is required."));
        }

        if (!CurrencyPattern.IsMatch(invoice.Currency))
        {
            issues.Add(new ExtractionValidationIssue("currency", "Currency must be a three-letter uppercase ISO code."));
        }

        if (invoice.InvoiceDate.HasValue && invoice.DueDate.HasValue && invoice.DueDate < invoice.InvoiceDate)
        {
            issues.Add(new ExtractionValidationIssue("dueDate", "Due date cannot be before invoice date."));
        }

        if (invoice.LineItems.Count == 0)
        {
            issues.Add(new ExtractionValidationIssue("lineItems", "At least one invoice line item is required."));
        }

        for (var index = 0; index < invoice.LineItems.Count; index++)
        {
            var item = invoice.LineItems[index];
            var path = $"lineItems[{index}]";
            if (string.IsNullOrWhiteSpace(item.Description))
            {
                issues.Add(new ExtractionValidationIssue($"{path}.description", "Description is required."));
            }

            if (item.Quantity <= 0)
            {
                issues.Add(new ExtractionValidationIssue($"{path}.quantity", "Quantity must be greater than zero."));
            }

            if (item.UnitPrice < 0 || item.LineTotal < 0)
            {
                issues.Add(new ExtractionValidationIssue(path, "Unit price and line total cannot be negative."));
            }

            var expectedLineTotal = decimal.Round(item.Quantity * item.UnitPrice, 2, MidpointRounding.AwayFromZero);
            if (Math.Abs(item.LineTotal - expectedLineTotal) > roundingTolerance)
            {
                issues.Add(new ExtractionValidationIssue($"{path}.lineTotal", $"Line total should be {expectedLineTotal:0.00} based on quantity and unit price."));
            }
        }

        var calculatedSubtotal = invoice.LineItems.Sum(item => item.LineTotal);
        if (Math.Abs(invoice.Subtotal - calculatedSubtotal) > roundingTolerance)
        {
            issues.Add(new ExtractionValidationIssue("subtotal", $"Subtotal should equal line totals ({calculatedSubtotal:0.00})."));
        }

        if (invoice.Tax < 0)
        {
            issues.Add(new ExtractionValidationIssue("tax", "Tax cannot be negative."));
        }

        if (invoice.Subtotal < 0 || invoice.Total < 0)
        {
            issues.Add(new ExtractionValidationIssue("total", "Subtotal and total cannot be negative."));
        }

        var calculatedTotal = invoice.Subtotal + invoice.Tax;
        if (Math.Abs(invoice.Total - calculatedTotal) > roundingTolerance)
        {
            issues.Add(new ExtractionValidationIssue("total", $"Total should equal subtotal plus tax ({calculatedTotal:0.00})."));
        }

        return issues;
    }
}