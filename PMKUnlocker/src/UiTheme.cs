// Central palette for PMK Mobile Tool — the single place a colour is named.
//
// Why this exists: panels were written at different times and drifted into near-duplicate
// shades ((22,25,30) vs (23,26,32) vs (24,27,33) for the same role, and so on), so one tab
// never quite matched another. The values below are the canonical set; the duplicates were
// snapped to these across src/. New code must take colours from here, not FromArgb literals.
//
// Roles (dark theme, the default):
//   Window  — form background, log area (deepest)
//   Sunken  — section wells: log panel edges, sub-headers, device rows
//   Card    — tab content panels, partition area
//   Raised  — inputs, grid rows, small cards (lightest surface)
//   Border  — hairlines and grid lines
//   Accent* — interactive/highlight tints (blue = informational, purple = brand)
//   Status  — log/status semantics: success, warning, danger, info
namespace PMKUnlocker
{
    internal static class UiTheme
    {
        // ---- surfaces (dark) ----
        public static readonly Color Window = Color.FromArgb(18, 20, 24);
        public static readonly Color Sunken = Color.FromArgb(22, 25, 30);
        public static readonly Color Section = Color.FromArgb(23, 26, 32);
        public static readonly Color Card = Color.FromArgb(26, 29, 35);
        public static readonly Color Raised = Color.FromArgb(35, 38, 48);
        public static readonly Color Border = Color.FromArgb(38, 42, 50);
        public static readonly Color BorderStrong = Color.FromArgb(45, 49, 58);
        public static readonly Color BorderLight = Color.FromArgb(60, 66, 76);
        public static readonly Color BorderMuted = Color.FromArgb(70, 80, 95);

        // ---- text ----
        public static readonly Color TextPrimary = Color.White;
        public static readonly Color TextBody = Color.Gainsboro;
        public static readonly Color TextMuted = Color.Gray;
        public static readonly Color TextSecondary = Color.FromArgb(150, 165, 185);

        // ---- accents ----
        // The brand accent is purple everywhere (login screen, header, tabs, links). The blue set
        // that used to live here was retired in favour of these; cyan stays a log/info colour and
        // is not used for interactive elements.
        public static readonly Color AccentBrand = Color.FromArgb(139, 92, 246);     // tabs, links, headers, selection
        public static readonly Color AccentBrandDeep = Color.FromArgb(124, 58, 237); // pressed / darker variant
        public static readonly Color AccentBrandSoft = Color.FromArgb(167, 139, 250);// hover rings, selected borders
        public static readonly Color AccentCyan = Color.FromArgb(0, 180, 255);       // log/info text highlights

        // ---- status (log and badges) ----
        public static readonly Color Success = Color.LightGreen;
        public static readonly Color SuccessStrong = Color.FromArgb(0, 230, 118);
        public static readonly Color Warning = Color.Orange;
        public static readonly Color WarningAmber = Color.FromArgb(255, 200, 90);
        public static readonly Color Danger = Color.OrangeRed;
        public static readonly Color DangerStrong = Color.Red;
        public static readonly Color Info = Color.Cyan;
    }
}
