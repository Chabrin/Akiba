using MudBlazor;

namespace Akiba.Web.Components.Layout;

/// <summary>
/// Akiba's design system: colour, type, spacing and elevation.
/// </summary>
/// <remarks>
/// <para>
/// This is a ledger read by four officials for hours at a time, usually with a paper form on
/// the desk beside the screen. That drives every decision here and none of them are taste:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Dense, not airy.</b> A consumer app can afford whitespace because somebody visits it for
/// ninety seconds. An official comparing a statement against a register wants as many rows on
/// screen as will stay legible, so the type scale and spacing are tightened well below the
/// framework's defaults.
/// </item>
/// <item>
/// <b>Borders, not shadows.</b> Heavy drop shadows read as decoration and make a dense screen
/// noisy. Surfaces are separated by a one-pixel line and a barely-there shadow, which is what
/// makes a table of figures look like a document rather than a pile of cards.
/// </item>
/// <item>
/// <b>Colour means something.</b> Green is the society, and everything else is semantic: amber
/// is waiting on somebody, red is money owed or a refusal, teal is informational. Nothing is
/// coloured to look nice. An official should be able to scan for red and find every problem.
/// </item>
/// <item>
/// <b>Tabular numerals, always.</b> Digits of equal width mean a figure out by a factor of ten
/// is visibly wrong before it is read. See <c>--akiba-font-numeric</c> in app.css.
/// </item>
/// </list>
/// <para>
/// The dark palette is not an inversion. It is built for a lit office at dusk rather than a
/// dark room, so the surfaces are warm charcoal rather than black and the green is lifted just
/// enough to hold its meaning without glowing.
/// </para>
/// </remarks>
public static class AkibaTheme
{
    // ---------------------------------------------------------------------
    // Colour
    // ---------------------------------------------------------------------
    //
    // A ramp rather than a handful of picks, so that every surface, border and muted label in
    // the application comes from the same place and stays consistent when one of them changes.

    private const string Green900 = "#14401A";
    private const string Green800 = "#1B5E20";
    private const string Green700 = "#22712A";
    private const string Green300 = "#7CC98A";
    private const string Green200 = "#A5DCAE";

    private const string Ink900 = "#12161A";
    private const string Ink800 = "#1B2127";
    private const string Ink700 = "#262E36";
    private const string Ink600 = "#39434D";
    private const string Ink500 = "#5A6672";
    private const string Ink400 = "#8A95A1";
    private const string Ink300 = "#B9C2CB";
    private const string Ink200 = "#DBE1E7";
    private const string Ink100 = "#EDF0F3";
    private const string Ink050 = "#F6F8F9";

    public static MudTheme Instance { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = Green800,
            PrimaryContrastText = "#FFFFFF",
            Secondary = Ink600,
            Tertiary = "#00695C",

            // The bar is the society's colour; everything below it is paper.
            AppbarBackground = Green900,
            AppbarText = "#FFFFFF",

            Background = Ink050,
            Surface = "#FFFFFF",
            DrawerBackground = "#FFFFFF",
            DrawerText = Ink700,
            DrawerIcon = Ink500,

            TextPrimary = Ink900,
            TextSecondary = Ink500,
            TextDisabled = Ink400,

            Divider = Ink200,
            DividerLight = Ink100,
            LinesDefault = Ink200,
            LinesInputs = Ink300,
            TableLines = Ink100,
            TableStriped = "#FAFBFC",
            TableHover = "#F0F5F1",

            ActionDefault = Ink500,
            ActionDisabled = Ink300,
            ActionDisabledBackground = Ink100,

            Success = "#2E7D32",
            Warning = "#B45309",
            Error = "#B3261E",
            Info = "#00695C",

            // Backgrounds for the chips and alerts, kept pale so a dense table does not stripe.
            SuccessLighten = "#E7F3E9",
            WarningLighten = "#FBF0E1",
            ErrorLighten = "#FBEAE9",
            InfoLighten = "#E3EFED",

            GrayLight = Ink100,
            GrayLighter = Ink050,
        },

        PaletteDark = new PaletteDark
        {
            Primary = Green300,
            PrimaryContrastText = Ink900,
            Secondary = Ink300,
            Tertiary = "#4DB6AC",

            AppbarBackground = "#10150F",
            AppbarText = "#E8EDE9",

            Background = Ink900,
            Surface = Ink800,
            DrawerBackground = "#151A1F",
            DrawerText = Ink200,
            DrawerIcon = Ink400,

            TextPrimary = "#E8ECEF",
            TextSecondary = Ink400,
            TextDisabled = Ink500,

            Divider = Ink700,
            DividerLight = Ink800,
            LinesDefault = Ink700,
            LinesInputs = Ink600,
            TableLines = Ink700,
            TableStriped = "#1F262D",
            TableHover = "#20302A",

            ActionDefault = Ink300,
            ActionDisabled = Ink600,
            ActionDisabledBackground = Ink700,

            Success = Green300,
            Warning = "#E0A458",
            Error = "#F2836B",
            Info = "#4DB6AC",

            SuccessLighten = "#1E2D21",
            WarningLighten = "#33281A",
            ErrorLighten = "#33211E",
            InfoLighten = "#16292B",

            GrayLight = Ink700,
            GrayLighter = Ink800,
        },

        // ---------------------------------------------------------------------
        // Type
        // ---------------------------------------------------------------------
        //
        // A deliberate scale. Stock sizes are tuned for marketing pages and leave a back-office
        // screen looking like a brochure with a table dropped into it.

        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = Sans,
                FontSize = "0.875rem",
                FontWeight = "400",
                LineHeight = "1.5",
                LetterSpacing = "0",
            },
            H1 = new H1Typography { FontFamily = Sans, FontSize = "1.75rem", FontWeight = "600", LineHeight = "1.25", LetterSpacing = "-0.02em" },
            H2 = new H2Typography { FontFamily = Sans, FontSize = "1.5rem", FontWeight = "600", LineHeight = "1.28", LetterSpacing = "-0.018em" },
            H3 = new H3Typography { FontFamily = Sans, FontSize = "1.25rem", FontWeight = "600", LineHeight = "1.3", LetterSpacing = "-0.014em" },
            H4 = new H4Typography { FontFamily = Sans, FontSize = "1.125rem", FontWeight = "600", LineHeight = "1.35", LetterSpacing = "-0.01em" },
            H5 = new H5Typography { FontFamily = Sans, FontSize = "1rem", FontWeight = "600", LineHeight = "1.4", LetterSpacing = "-0.006em" },
            H6 = new H6Typography { FontFamily = Sans, FontSize = "0.875rem", FontWeight = "600", LineHeight = "1.45", LetterSpacing = "0" },

            Subtitle1 = new Subtitle1Typography { FontFamily = Sans, FontSize = "0.875rem", FontWeight = "600", LineHeight = "1.45" },
            Subtitle2 = new Subtitle2Typography { FontFamily = Sans, FontSize = "0.8125rem", FontWeight = "600", LineHeight = "1.45" },

            Body1 = new Body1Typography { FontFamily = Sans, FontSize = "0.8125rem", FontWeight = "400", LineHeight = "1.55" },
            Body2 = new Body2Typography { FontFamily = Sans, FontSize = "0.78125rem", FontWeight = "400", LineHeight = "1.55" },

            // Buttons are not shouted at. Upper-casing a label makes it harder to read and
            // makes every action look equally urgent, which on this system they are not.
            Button = new ButtonTypography { FontFamily = Sans, FontSize = "0.8125rem", FontWeight = "600", LineHeight = "1.75", TextTransform = "none", LetterSpacing = "0.005em" },

            Caption = new CaptionTypography { FontFamily = Sans, FontSize = "0.75rem", FontWeight = "400", LineHeight = "1.45" },

            // The small label above a figure. Letter-spaced, because a short upper-case string
            // is unreadable without it.
            Overline = new OverlineTypography { FontFamily = Sans, FontSize = "0.6875rem", FontWeight = "600", LineHeight = "1.6", LetterSpacing = "0.075em", TextTransform = "uppercase" },
        },

        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
            DrawerWidthLeft = "244px",
            AppbarHeight = "56px",
        },

        // Shadows carry information here rather than depth. Everything below the menu layer is
        // flat and separated by a line; only things that genuinely float get a real shadow.
        Shadows = new Shadow
        {
            Elevation = BuildElevations(),
        },
    };

    /// <summary>
    /// Century Gothic, with the nearest thing available where it is not installed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Century Gothic ships with Microsoft Office, so it is on the office machines and is very
    /// likely absent anywhere else. The fallbacks are the same geometric shape rather than the
    /// nearest system font, so a machine without it gets something that still looks deliberate:
    /// URW Gothic on Linux, then Questrial, then Futura, before giving up.
    /// </para>
    /// <para>
    /// Worth knowing what it costs. Century Gothic is a geometric face with a very low x-height
    /// and unusually wide letterforms - it was drawn for display, not for a dense table of
    /// figures. Text sits about 6% wider than Inter at the same size, so a long member name is
    /// likelier to be cut off, and the small print reads smaller than its size suggests. That is
    /// paid for here by nudging the base size up a little rather than by shrinking the layout.
    /// </para>
    /// <para>
    /// The money figures are the part to watch. See <c>--akiba-font-numeric</c> in app.css.
    /// </para>
    /// </remarks>
    private static readonly string[] Sans =
    [
        "Century Gothic", "URW Gothic", "Questrial", "Futura", "Segoe UI", "Roboto",
        "sans-serif",
    ];

    /// <summary>
    /// The elevation ramp.
    /// </summary>
    /// <remarks>
    /// MudBlazor expects 26 entries. The low ones are deliberately almost invisible - a card at
    /// elevation 1 should read as a sheet of paper on a desk, not as something hovering above
    /// it - and only the menu and dialog levels get a shadow anybody would notice.
    /// </remarks>
    private static string[] BuildElevations()
    {
        var elevations = new string[26];

        elevations[0] = "none";
        elevations[1] = "0 1px 1px rgba(16,24,32,.04), 0 0 0 1px rgba(16,24,32,.06)";
        elevations[2] = "0 1px 2px rgba(16,24,32,.06), 0 0 0 1px rgba(16,24,32,.07)";
        elevations[3] = "0 2px 4px rgba(16,24,32,.07), 0 0 0 1px rgba(16,24,32,.07)";
        elevations[4] = "0 4px 8px rgba(16,24,32,.08), 0 0 0 1px rgba(16,24,32,.07)";

        for (var level = 5; level < 26; level++)
        {
            // Menus, dialogs and the drawer in overlay mode. These do float, so they cast.
            var spread = 4 + (level - 4);
            elevations[level] =
                $"0 {spread}px {spread * 2}px rgba(16,24,32,.12), 0 0 0 1px rgba(16,24,32,.07)";
        }

        return elevations;
    }
}
