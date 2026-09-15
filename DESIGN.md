---
name: The League
description: A spirit-duplicated club bulletin for an eight-team keeper league.
colors:
  stencil-ground: "#1c1720"
  stencil-raised: "#241e29"
  stencil-sunk: "#15111a"
  aniline-ink: "#e9e2f4"
  aniline-quiet: "#b3a7c9"
  aniline-faint: "#8b7fa3"
  aniline-violet: "#bda4f2"
  aniline-violet-deep: "#8f6fe0"
  correction-stamp: "#f0766a"
  correction-stamp-deep: "#d14a39"
  rule-hairline: "#3d3449"
  rule-struck: "#5c4f72"
  sheet-ground: "#e5e3db"
  sheet-raised: "#eeece5"
  sheet-sunk: "#d6d3c8"
  sheet-ink: "#262032"
  sheet-quiet: "#4c4265"
  sheet-faint: "#6b6182"
  sheet-violet: "#452f8d"
  sheet-violet-deep: "#2f1d65"
  sheet-stamp: "#a92f21"
  sheet-stamp-deep: "#7f1f14"
  sheet-rule: "#b7b2a5"
  sheet-rule-struck: "#8a8477"
typography:
  masthead:
    fontFamily: "Courier Prime, Courier New, ui-monospace, monospace"
    fontSize: "clamp(2.1rem, 11vw, 6.5rem)"
    fontWeight: 700
    lineHeight: 0.92
    letterSpacing: "-0.045em"
  display:
    fontFamily: "Courier Prime, Courier New, ui-monospace, monospace"
    fontSize: "clamp(1.85rem, 6.6vw, 3.6rem)"
    fontWeight: 700
    lineHeight: 1.12
    letterSpacing: "-0.02em"
  headline:
    fontFamily: "Courier Prime, Courier New, ui-monospace, monospace"
    fontSize: "clamp(1.35rem, 3.6vw, 1.95rem)"
    fontWeight: 700
    lineHeight: 1.12
    letterSpacing: "0.005em"
  title:
    fontFamily: "Courier Prime, Courier New, ui-monospace, monospace"
    fontSize: "clamp(1.1rem, 2.8vw, 1.35rem)"
    fontWeight: 700
    lineHeight: 1.12
  body:
    fontFamily: "Courier Prime, Courier New, ui-monospace, monospace"
    fontSize: "1.0625rem"
    fontWeight: 400
    lineHeight: 1.7
  ledger:
    fontFamily: "Courier Prime, Courier New, ui-monospace, monospace"
    fontSize: "0.92rem"
    fontWeight: 400
    lineHeight: 1.5
    fontFeature: "tabular-nums"
  label:
    fontFamily: "Courier Prime, Courier New, ui-monospace, monospace"
    fontSize: "0.72rem"
    fontWeight: 400
    lineHeight: 1.5
    letterSpacing: "0.2em"
rounded:
  none: "0"
spacing:
  gutter: "clamp(1.15rem, 4vw, 3rem)"
  measure: "66ch"
  sheet-max: "72rem"
  sheet-narrow: "50rem"
components:
  nav-link:
    textColor: "{colors.aniline-quiet}"
    typography: "{typography.label}"
    rounded: "{rounded.none}"
  nav-link-current:
    textColor: "{colors.aniline-ink}"
    typography: "{typography.label}"
  sheet-toggle:
    backgroundColor: "transparent"
    textColor: "{colors.aniline-quiet}"
    typography: "{typography.label}"
    rounded: "{rounded.none}"
    padding: "0.45rem 0.8rem"
  ledger-row:
    backgroundColor: "transparent"
    textColor: "{colors.aniline-ink}"
    typography: "{typography.ledger}"
    rounded: "{rounded.none}"
    padding: "0.5rem 0"
---

# Design System: The League

## Overview

**Creative North Star: "The Mimeographed Club Bulletin"**

This is not a sports website wearing a retro texture. It is the artifact itself: the stapled,
typewritten sheet a commissioner ran off on a spirit duplicator and mailed to eight members
before any of this lived on the web. Every decision follows from asking what that office
actually had, and refusing what it did not.

That office had **one typewriter**, so the site has one typeface — Courier Prime — including
the masthead, which is not a display font but the same typebar struck at size. It had **two
inks**, aniline violet and a correction-stamp red, so the site has two. It had **no rounded
corners, no shadows, and no gradients**, because a duplicator lays flat ink on flat stock, so
the site has none of those either. Monospace here is earned rather than costumed: it is the
world's native output, and it delivers free tabular alignment to the standings and scoring
ledgers that carry most of the page.

The dark rendition is primary because the real use scene is a phone on a Tuesday night with
the lights low. Critically, **dark is not an inversion of light** — it is the wax stencil, the
dark sheet the type is cut through. Light is the printed copy that comes off it. Both are real
states of one physical process, which is why the palette does not simply flip.

**Key Characteristics:**

- One typeface, everywhere, masthead included
- Two inks; no third color is ever introduced
- Zero radius, zero shadow, zero gradient
- Rules and struck lines carry all structure
- Ink-on-paper grain over the whole surface at low strength
- Numbers render from data and are always tabular

## Colors

Two inks on one stock, plus the stock's own tonal range. Nothing else.

### Primary
- **Aniline Violet** (`#bda4f2` stencil / `#452f8d` sheet): The duplicator's ink. Links, active
  navigation, rules that need to assert, and the masthead's struck offset. Bright enough on the
  stencil to read as freshly cut.

### Secondary
- **Correction Stamp** (`#f0766a` stencil / `#a92f21` sheet): The red pad the commissioner kept
  on the desk. Reserved for results and corrections — a champion's place number, a loss, a
  record-book extreme. It is never decoration and never a brand accent.

### Neutral
- **Stencil Ground** (`#1c1720`): Deep aubergine-brown wax. The primary surface. Deliberately
  not black.
- **Stencil Raised / Sunk** (`#241e29` / `#15111a`): Banded sections and the nav rail. Tonal
  only; no shadow separates them.
- **Aniline Ink / Quiet / Faint** (`#e9e2f4` / `#b3a7c9` / `#8b7fa3`): Body copy, secondary
  copy, and slugs.
- **Sheet Ground** (`#e5e3db`): Cool grey duplicator copy stock. **Not cream, not vellum** —
  warm paper is the default AI rut this world explicitly rejects.
- **Hairline / Struck Rule** (`#3d3449` / `#5c4f72`): The two rule weights that do all
  structural work in place of borders and cards.

### Named Rules

**The Two-Ink Rule.** Violet and stamp red are the entire chromatic vocabulary. If a new state
needs distinguishing, use a rule weight, a case change, or tonal ground — never a third hue.

**The Stamp-Is-A-Result Rule.** Correction red marks something that happened in a game: a
title, a loss, a record. It never marks a button, a hover, or a section merely because that
section is important.

**The Plate Exemption.** The photocopied chart plates keep their original eight series colors.
Those colors are load-bearing data encoding, not decoration, and flattening them to two inks
would destroy information. Only the chart *chrome* is neutralized into the world.

## Typography

**Display Font:** Courier Prime (with Courier New, ui-monospace, monospace)
**Body Font:** Courier Prime — the same face
**Label/Mono Font:** Courier Prime — the same face

**Character:** One typewriter, three registers. Weight, size, case, and letter-spacing do all
the work that a second family would otherwise do. The result reads as a single hand typing the
whole bulletin, which is exactly the fiction.

### Hierarchy
- **Masthead** (700, `clamp(2.1rem, 11vw, 6.5rem)`, 0.92, `-0.045em`, uppercase): The nameplate
  only. Carries a hard `0.012em` violet offset — the typebar hit the stencil hard enough to
  spread. This is the one place ink behavior is simulated.
- **Display / h1** (700, `clamp(1.85rem, 6.6vw, 3.6rem)`, 1.12, uppercase): Page and article
  titles.
- **Headline / h2** (700, `clamp(1.35rem, 3.6vw, 1.95rem)`, uppercase): Section heads, usually
  paired with a struck rule.
- **Title / h3** (700, `clamp(1.1rem, 2.8vw, 1.35rem)`): Sub-sections and card heads. Not
  uppercased — the case change is what separates it from h2.
- **Body** (400, `1.0625rem`, 1.7): Prose, capped at a `66ch` measure.
- **Ledger** (400, `0.92rem`, tabular-nums): Every table of scores, standings, and records.
- **Label / slug** (400, `0.72rem`, `0.2em`, uppercase): Kickers, datelines, metadata, column
  notes.

### Named Rules

**The One Face Rule.** Courier Prime is the only family, and the masthead is not exempt. A
display face here would be a costume the duplicator office could not have worn.

**The Tabular Rule.** Any figure a reader might compare down a column renders with
`font-variant-numeric: tabular-nums`. Scores, records, and points are always comparable.

**The Uppercase Ladder.** Uppercase marks the masthead, h1, h2, and labels. h3 and body stay
sentence case, so case itself signals depth.

## Layout

A single-column bulletin that widens into a two-column front page, never into a dashboard.

- **Sheet container:** `min(72rem, 100%)` with a `clamp(1.15rem, 4vw, 3rem)` gutter.
  `.sheet--narrow` drops to `min(50rem, 100%)` for reading-first routes.
- **Measure:** paragraphs cap at `66ch`. Tables are exempt and may use the full sheet.
- **Front page:** lead story and "also in this issue" rail sit side by side on desktop and
  stack on mobile, lead first.
- **Banding:** alternate sections take `--ground-raised` or `--ground-sunk`. Tonal banding, not
  cards, is how the page segments.
- **Rhythm:** vertical spacing derives from the type scale rather than a fixed 8px grid, which
  suits a typewritten sheet where the line advance is the grid.
- **Tables on mobile:** wrapped in `.ledger-scroll`, the one place horizontal scroll is
  sanctioned. Prose tables from markdown get the same treatment.

## Elevation & Depth

**This system has no shadows and no elevation.** A spirit duplicator lays flat ink on flat
stock; nothing floats. Depth is conveyed by three means only:

1. **Tonal ground** — `--ground-sunk` and `--ground-raised` band sections apart.
2. **Rules** — a hairline separates, a struck rule declares.
3. **Grain** — a fixed SVG turbulence overlay at low opacity sits above the whole page,
   unifying every surface into one sheet.

The single exception is the masthead's `text-shadow`, which is not elevation. It is ink spread
from the typebar strike, hard-offset with no blur.

### Named Rules

**The Flat Stock Rule.** No `box-shadow`, no gradient, no blur, no glow, anywhere. If an
element needs separation, band it or rule it.

## Shapes

**Radius is zero, globally.** Nothing on a duplicated sheet has a rounded corner. Buttons,
toggles, tables, and plates are all hard-cornered rectangles.

Form language is rules, not containers. There are exactly two rule weights — a 1px hairline
(`--rule`) for separation and a 2px struck rule (`--rule-strong`) for declaration — plus a
dotted variant for list rows, which reads as the perforated tear-line of a continuous-feed
sheet. Cards do not exist as a concept; a "card" here is a titled block bounded by a rule.

## Components

### Buttons and the Sheet Toggle
- **Shape:** Hard rectangle (radius `0`), 1px `--rule-strong` border.
- **Primary:** Transparent ground, `--ink-quiet` text, `0.45rem 0.8rem` padding, label
  typography. The nameplate-adjacent PRINT control is the canonical instance.
- **Hover / Focus:** Border and text step to full `--ink`. No fill, no lift, no transition
  longer than a stamp press.

### Ledgers (the signature component)
The standings, scoreboard, and record tables are the heart of the site, so they are designed
rather than dumped.
- **Structure:** Full-width, `border-collapse: collapse`, hairline row rules only. No zebra
  striping, no outer border, no cell borders.
- **Numbers:** Right-aligned and tabular, always.
- **Caption:** Left-aligned label typography above the table, functioning as a column head.
- **Overflow:** `.ledger-scroll` wrapper on narrow viewports.

### Blocks / Containers
- **Corner Style:** `0`.
- **Background:** Inherits the section band; blocks do not carry their own fill.
- **Shadow Strategy:** None. See Elevation.
- **Border:** A top struck rule where the block needs to announce itself; otherwise nothing.

### Navigation
Label typography, uppercase, wide-tracked, laid out as a horizontal rail on a banded ground.
The current route takes full `--ink` while the rest sit at `--ink-quiet`; no underline, no
pill, no indicator bar. On mobile the rail scrolls horizontally rather than collapsing into a
menu — a bulletin's contents line does not fold into a hamburger.

### Chart Plates
Chart SVGs are treated as photocopied plates pasted onto the sheet. The chrome (backgrounds,
axis greys) is neutralized into the world's palette, while the eight data series keep their
source colors under the Plate Exemption. Each plate carries a `<details>` text-equivalent
table so the same ranking is available without seeing color.

## Do's and Don'ts

### Do:
- **Do** render every number from `league.json`. Prose explains; data decides.
- **Do** use a rule weight or a tonal band when something needs separation.
- **Do** keep correction red for results — titles, losses, extremes.
- **Do** set comparable figures in tabular numerals.
- **Do** state the archive's scope honestly. The league predates the data; the site says
  "on record", never "all-time", and never claims a founding year.
- **Do** name members by first name only. This is enforced by `PrivacyGuardTests`, which fails
  the build on a surname or Sleeper username in `recaps/` or `site/`.

### Don't:
- **Don't** add a second typeface, including for the masthead.
- **Don't** add a `box-shadow`, gradient, blur, or border-radius. The stock is flat.
- **Don't** introduce a third color. Not for success, not for warning, not for a chart.
- **Don't** warm the light sheet toward cream. It is cool grey `#e5e3db` on purpose.
- **Don't** flatten the chart plates' series colors — that is data, not decoration.
- **Don't** use uppercase below h2 except for labels and slugs; the case ladder is structural.
