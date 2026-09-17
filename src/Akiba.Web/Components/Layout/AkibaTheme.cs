using MudBlazor;

namespace Akiba.Web.Components.Layout;

/// <summary>
/// Akiba's palette and typography.
/// </summary>
/// <remarks>
/// Deliberately sober. This is a back-office ledger read by four officials, often while
/// comparing the screen with a paper form on the desk - so the green is the dark, flat green
/// of a bank passbook rather than anything bright, and the numerals are tabular so that a
/// figure out by a factor of ten looks wrong at a glance.
/// </remarks>
public static class AkibaTheme
{
    public static MudTheme Instance { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#1b5e20",
            Secondary = "#37474f",
            AppbarBackground = "#1b5e20",
            Background = "#f7f8f7",
            DrawerBackground = "#ffffff",
            Success = "#2e7d32",
            Warning = "#ef6c00",
            Error = "#c62828",
            Info = "#00695c",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#66bb6a",
            Secondary = "#90a4ae",
            AppbarBackground = "#1b2419",
            Success = "#66bb6a",
            Warning = "#ffa726",
            Error = "#ef5350",
            Info = "#4db6ac",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = ["Segoe UI", "Roboto", "Helvetica", "Arial", "sans-serif"] },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "6px",
            DrawerWidthLeft = "250px",
        },
    };
}
