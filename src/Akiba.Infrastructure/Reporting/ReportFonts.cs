namespace Akiba.Infrastructure.Reporting;

/// <summary>
/// The font Akiba's PDFs are set in.
/// </summary>
/// <remarks>
/// Lato, because QuestPDF bundles it and it is therefore present wherever Akiba runs. Naming a
/// system font instead - Calibri, say - produces documents that render on a Windows
/// development machine and throw on the Linux box in the office, which is the worst possible
/// place to find out.
/// </remarks>
internal static class ReportFonts
{
    public const string Body = "Lato";
}
