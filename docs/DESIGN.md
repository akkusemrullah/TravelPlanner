# TravelPlanner design system

Royal blue, flat and corporate — the palette follows the Travel Planner logo (map pin + paper plane):
Royal Blue `#1E3A8A`, Bright Blue `#2563EB`, White. Logo sources: `docs/brand/`. Everything below lives in `Resources/Styles` and `Resources/Themes`;
pages only reference keys, never raw colours or font files.

## Colour tokens (`tp-*`)

Defined three times from one table (`DesignTokens.xaml` = static light defaults, `Themes/LightTheme.xaml`,
`Themes/DarkTheme.xaml`). Always use them with `{DynamicResource ...}` so light/dark switching works live.

| Token | Light | Dark | Use |
|---|---|---|---|
| `tp-bg` | #F3F4F6 | #0E1013 | Page canvas |
| `tp-surface` | #FFFFFF | #16191E | Cards, app bars, tab bar |
| `tp-surface-alt` | #F7F8FA | #1C2026 | Icon tiles, subtle fills |
| `tp-border` / `tp-border-strong` | #E2E5EA / #C9CED6 | #272B33 / #363C46 | Card strokes / input strokes |
| `tp-text` / `-2` / `-3` | #14161B / #4A505C / #6B7280 | #F1F2F4 / #B3B9C4 / #858C99 | Primary / secondary / captions |
| `tp-primary` | #1E3A8A | #2563EB | Royal blue primary buttons |
| `tp-accent` | #2563EB | #2F6FED | Bright blue brand accent (FAB, selected tab, highlights) |
| `tp-success` / `warning` / `danger` / `info` (+ `-soft`) | | | Status text / tinted backgrounds (info is teal #0E7490 so it never reads as the brand blue) |
| `tp-hero` | #1E3A8A | #172554 | Royal blue summary cards |

Legacy `fds-*` keys still exist and map onto these values.

## Typography — Inter (SIL OFL)

| Style | Font | Size |
|---|---|---|
| `Text.Display` | Inter Bold | 28 |
| `Text.H1` | Inter Bold | 24 |
| `Text.H2` / `Text.H3` | Inter SemiBold | 18 / 16 |
| `Text.Title` | Inter SemiBold | 15 |
| `Text.Body` / `Text.Body2` | Inter Regular | 15 / 14 |
| `Text.Caption` | Inter Regular | 12 |
| `Text.Overline` | Inter SemiBold, uppercase, tracked | 11 |
| `Text.Field` | Inter Medium | 13 (form labels) |
| `Text.Metric` | Inter Bold | 22 (KPIs) |

## Icons — Lucide (ISC)

One stroke icon font (`Resources/Fonts/Lucide.ttf`, subset). Glyphs are keyed in `Icons.xaml` (`Ic.Home`,
`Ic.Trips`, …) and drawn with the `Icon` / `Icon.Sm` / `Icon.Xs` / `Icon.Lg` label styles (20 / 16 / 14 / 24).
The tab bar uses the same glyphs through `FontImageSource`. To add an icon: add it to `ICONS` in the generator,
regenerate `Icons.xaml` and re-subset the font.

## Components

* `Card`, `Card.Compact`, `Card.List` (rows with inset dividers), `Card.Hero` (royal blue), `Card.Flat`
* `AppBar` (pushed pages: back/close · title · actions) and `PageHeader` (tab roots: overline + large title)
* `Input` / `Input.Multiline` — field chrome around transparent Entry / Picker / DatePicker / Editor
* `Btn.Primary`, `Btn.Accent`, `Btn.Secondary`, `Btn.Ghost`, `Btn.Danger` (+ `.Sm`), `Btn.OnHero`
* `IconButton` (+ `.Outlined`, `.Filled`), `IconTile` (+ `.Sm`, `.Accent`), `Fab`
* `Tag` + `Tag.Text`, `Chip` (filters), `Banner.Info/Warning/Error`, `Divider`, `Divider.Inset`, `EmptyIcon`

## Layout rules

* 4-pt grid: 4 / 8 / 12 / 16 / 20 / 24. Page gutters 16, section gap 24, card padding 16 (12 compact).
* Radii: 14 hero, 12 cards, 10 controls, 8 small controls, 6 tags.
* Flat surfaces with 1 px borders — no gradients, no drop shadows, no emoji in UI text.
* Navigation: bottom tab bar (Home · Trips · Profile); every other page is pushed and hides the tab bar.
* Forms end with a sticky action bar: secondary *Cancel* + primary *Save*, validation banner above.
