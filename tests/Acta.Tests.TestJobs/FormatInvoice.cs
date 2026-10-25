using Acta;

namespace TestJobs;

public sealed record InvoiceLine(string Text);

/// <summary>
/// A handler taking several input parameters: its input is the generated <c>TestJobsManifest.FormatInvoiceArgs</c>,
/// stored as one JSON object of the parameters.
/// </summary>
public static class FormatInvoiceHandler
{
    [Job("format-invoice")]
    public static InvoiceLine Run(int invoiceId, string customer) => new($"{customer}-{invoiceId}");
}
