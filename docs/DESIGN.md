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
| `tp-hero` | #1E3A8A | #172554 | Royal blue summary cards, boarding-pass stub |
| `tp-ticket-start` / `tp-ticket-end` | #1E3A8A / #2F5BD3 | #172554 / #1E3170 | Boarding-pass gradient (top half) |

Trip status tags (`TripTagColorConverter`): ongoing green #1B7543 on #E7F4EC (dark #4CC27E on #13281C), upcoming teal
#0E7490 on #E3F3F6 (dark #22B8CF on #0F2A30), completed neutral #4A505C on #EEF0F3 (dark #B3B9C4 on #1C2026).

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
regenerate `Icons.xaml` and re-subset the font. Icons added with the 2026 redesign (TP-96, `Ic.Luggage` … `Ic.ShieldCheck`)
come from lucide-static 1.62.0 and sit at U+E900+ so they never clash with the older subset's code points;
`DesignSystemTests` checks every `Ic.*` key has a glyph.

## Components

* `Card` (16), `Card.Compact`, `Card.List` (rows with inset dividers), `Card.Hero` (royal blue, 20), `Card.Flat` (12), `Card.Ticket` (22)
* `Views/TripTicketView` — the boarding pass (Home featured trip, Trip detail): gradient top with status pill, countdown,
  city, departure ✈ return (the plane rides the route line with the trip's progress), perforated tear line with notches,
  stub with duration / budget and Hava · Harita · Detaylar buttons (only those with a command show)
* `AppBar` (pushed pages: back/close · title · actions) and `PageHeader` (tab roots: large title on the page colour)
* `Input` / `Input.Multiline` — field chrome around transparent Entry / Picker / DatePicker / Editor
* `Btn.Primary`, `Btn.Accent`, `Btn.Secondary`, `Btn.Soft`, `Btn.Ghost`, `Btn.Danger` (soft red, + `.Sm`), `Btn.OnHero` (+ `.Outline`)
* `IconButton` (round 44, + `.Outlined`, `.Filled`), `IconTile` (+ `.Sm`, `.Tint`, `.Neutral`, `.Accent`, status tones), `Fab`
* `Tag` + `Tag.Text` (pill), `Chip` (filters, may carry a count), `Banner.Info/Warning/Error/Accent/Success`, `Divider`, `Divider.Inset`, `EmptyIcon`
* Dialogs (`AppDialogPage`): centred card with icon tile, title, message and stacked buttons; bottom sheet with handle,
  title + close button and roomy option rows
* `Controls/FloatingTabBar` (TP-106): the bottom menu - a rounded surface bar 16 from the edges (Ana Sayfa · Seyahatler ·
  Keşfet · Profil, selected item in `tp-nav-active`) with a round `tp-accent` "+" (new trip) standing 20 above its middle.
  Each tab root page sets `Shell.TabBarIsVisible="False"`, puts the bar last in its root Grid and leaves ~104 at the bottom of
  its scroll content. Tabs switch through `Helpers/TabNavigation` (same as a Shell tab tap; TP-90 back history still works)
* Upper case (TP-106): never `TextTransform="Uppercase"` (MAUI uses the invariant culture: "GIDIŞ"). Use
  `{helpers:Upper Key}` for resource strings, `Converter={StaticResource UpperText}` for bindings or `TextCase.Upper` in
  code; Turkish casing unless the app language is English. A unit test fails on any `TextTransform` in XAML
* Controls (TP-100): `ListRow` (tinted icon tile, title, subtitle, chevron, optional trailing view), `Avatar` (initials on a
  soft tone picked from the name, `PersonName.ToneIndex`), `Badge` (24 high pill, tone + optional icon), `CheckMark`
  (square or round tick), `SelectCircle` (selection mode), `ProgressPill` (thin bar, `Tone` or `FillColor`)
* Grouped list cards (TP-102): a CollectionView can't wrap rows in one Border, so every row is a slice
  (`ListPosition` → `ListPositionShapeConverter` / `ListPositionMarginConverter`). `ListPositions.Track(collection)`
  keeps positions right; `ListPositions.AssignCards(rows, startsCard)` splits one flat list into several cards
  (AI plan preview, Keşfet detail: a day header starts a new card, a section title ends the one above)

## Layout rules

* 4-pt grid: 4 / 8 / 12 / 16 / 20 / 24. Page gutters 16, section gap 24, card padding 16 (12 compact).
* Radii: 22 ticket, 20 hero, 16 cards, 12 controls / buttons / icon tiles, pill tags and chips.
* Flat surfaces with 1 px borders; the boarding pass and `Card.Hero` are the only gradients, the ticket and FAB the only shadows. No emoji in UI text.
* Navigation: floating bottom menu (Home · Trips · + · Explore · Profile) on the tab roots; every other page is pushed and hides it.
* Home (TP-106): blue hero (`tp-hero`, rings + dashed flight path) with "Roma'ya **12 gün** kaldı" (`tp-hero-accent`,
  Turkish endings from `Helpers/PlaceSuffix`), a white boarding-pass card over its edge, quick-link chips, "Seyahat
  takvimin" (date tiles), "Seyahat karnen". Without a coming trip: "Bir sonraki durağın neresi?" + city box (opens the
  trip form with `?City=`), and with no trips at all "3 adımda yola hazır", Keşfet plans (no budget) and feature cards.
* Forms end with a sticky action bar: secondary *Cancel* + primary *Save*, validation banner above.
