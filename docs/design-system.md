# Design System — Aegis Command

Status: **authoritative for UI work**. Exported verbatim from the Stitch project
`Knight Super Admin Control Plane` (project 10363262931731977567) on 2026-08-18.

This file is the source of truth for colours, typography, spacing, elevation,
shape and component behaviour. Deliberate deviations from this export (icon
library, Persian typeface, no CDNs, light palette) are recorded in
[`frontend-architecture.md`](frontend-architecture.md) §11.

---

---
name: Aegis Command
colors:
  surface: '#13131b'
  surface-dim: '#13131b'
  surface-bright: '#393841'
  surface-container-lowest: '#0d0d15'
  surface-container-low: '#1b1b23'
  surface-container: '#1f1f27'
  surface-container-high: '#292932'
  surface-container-highest: '#34343d'
  on-surface: '#e4e1ed'
  on-surface-variant: '#c7c4d7'
  inverse-surface: '#e4e1ed'
  inverse-on-surface: '#303038'
  outline: '#908fa0'
  outline-variant: '#464554'
  surface-tint: '#c0c1ff'
  primary: '#c0c1ff'
  on-primary: '#1000a9'
  primary-container: '#8083ff'
  on-primary-container: '#0d0096'
  inverse-primary: '#494bd6'
  secondary: '#b9c8de'
  on-secondary: '#233143'
  secondary-container: '#39485a'
  on-secondary-container: '#a7b6cc'
  tertiary: '#ffb783'
  on-tertiary: '#4f2500'
  tertiary-container: '#d97721'
  on-tertiary-container: '#452000'
  error: '#ffb4ab'
  on-error: '#690005'
  error-container: '#93000a'
  on-error-container: '#ffdad6'
  primary-fixed: '#e1e0ff'
  primary-fixed-dim: '#c0c1ff'
  on-primary-fixed: '#07006c'
  on-primary-fixed-variant: '#2f2ebe'
  secondary-fixed: '#d4e4fa'
  secondary-fixed-dim: '#b9c8de'
  on-secondary-fixed: '#0d1c2d'
  on-secondary-fixed-variant: '#39485a'
  tertiary-fixed: '#ffdcc5'
  tertiary-fixed-dim: '#ffb783'
  on-tertiary-fixed: '#301400'
  on-tertiary-fixed-variant: '#703700'
  background: '#13131b'
  on-background: '#e4e1ed'
  surface-variant: '#34343d'
typography:
  display:
    fontFamily: Hanken Grotesk
    fontSize: 48px
    fontWeight: '700'
    lineHeight: 56px
    letterSpacing: -0.02em
  headline-lg:
    fontFamily: Hanken Grotesk
    fontSize: 32px
    fontWeight: '600'
    lineHeight: 40px
  headline-lg-mobile:
    fontFamily: Hanken Grotesk
    fontSize: 24px
    fontWeight: '600'
    lineHeight: 32px
  title-md:
    fontFamily: Hanken Grotesk
    fontSize: 20px
    fontWeight: '600'
    lineHeight: 28px
  body-md:
    fontFamily: Hanken Grotesk
    fontSize: 16px
    fontWeight: '400'
    lineHeight: 24px
  body-sm:
    fontFamily: Hanken Grotesk
    fontSize: 14px
    fontWeight: '400'
    lineHeight: 20px
  label-caps:
    fontFamily: JetBrains Mono
    fontSize: 12px
    fontWeight: '500'
    lineHeight: 16px
    letterSpacing: 0.05em
  code:
    fontFamily: JetBrains Mono
    fontSize: 13px
    fontWeight: '400'
    lineHeight: 18px
rounded:
  sm: 0.125rem
  DEFAULT: 0.25rem
  md: 0.375rem
  lg: 0.5rem
  xl: 0.75rem
  full: 9999px
spacing:
  unit: 8px
  xs: 4px
  sm: 8px
  md: 16px
  lg: 24px
  xl: 32px
  container-margin: 24px
  gutter: 16px
---

## Brand & Style

The design system is engineered for high-stakes enterprise administration, emphasizing security, precision, and authority. The brand personality is "The Invisible Sentinel"—robust and reliable without being intrusive. 

The aesthetic follows a **Corporate / Modern** direction with a focus on **technical geometry**. It utilizes a structured grid, subtle elevation to denote hierarchy, and a restrained use of color to highlight critical data and system statuses. The interface must feel "hardened" yet frictionless, catering to IT directors and security officers who manage complex infrastructures. Visual motifs should include micro-patterns (e.g., subtle dot grids) and thin, precise strokes that evoke the feeling of a sophisticated control deck.

## Colors

This design system utilizes a dual-mode palette optimized for long-duration monitoring.

**Light Mode:** Built on a foundation of neutral grays (#F9FAFB) to reduce eye strain, using pure white for elevated surfaces. The deep indigo (#4F46E5) provides a strong authoritative contrast for primary actions.

**Dark Mode (Default):** Uses a near-black cool-toned navy (#0F172A) for deep backgrounds, with elevated surfaces using a lighter slate (#1E293B). The indigo accent is shifted to a more vibrant value (#6366F1) to ensure WCAG AA compliance against dark backgrounds.

**Semantic Colors:** Status colors are high-chroma but used sparingly to prevent visual noise. They should only appear in icons, badges, or thin border-accents to signal system health.

## Typography

The typography system prioritizes legibility in data-dense environments. **Hanken Grotesk** is the primary typeface for its modern, precise Grotesk qualities and excellent support for Latin characters alongside Persian-friendly metrics. 

**RTL Support:** For Persian (Farsi) users, the font-stack must fall back to a clean, modern Sans-Serif like **Vazirmatn** or **Peyda**. Line heights for Persian text should be increased by 15-20% compared to Latin counterparts to accommodate taller ascenders and descenders.

**Technical Accents:** **JetBrains Mono** is used for labels, metadata, and ID strings to reinforce the technical/secure nature of the control plane.

## Layout & Spacing

The design system is based on a strict **8px linear scale**. All components and layouts should align to this rhythm to maintain mathematical harmony.

**Grid Philosophy:** 
- **Desktop:** 12-column fluid grid with 16px gutters and 24px side margins. 
- **Sidebar:** A fixed-width sidebar (280px) that can collapse to a mini-variant (64px).
- **RTL Behavior:** The layout must mirror horizontally. Sidebars move to the right, and the content flow begins from the right. Icons that denote direction (arrows, chevrons) must be flipped, while semantic icons (shield, clock, lock) remain unflipped.

## Elevation & Depth

Hierarchy is established through **Tonal Layering** supplemented by **Precise Outlines**. 

- **Level 0 (Background):** Base color of the mode.
- **Level 1 (Card/Surface):** +1 tonal step from background with a subtle 1px border (Opacity: 10% white in dark mode; 5% black in light mode).
- **Level 2 (Dropdowns/Modals):** Slight elevation using "Ambient Shadows"—diffused, low-opacity shadows with a slight navy tint (#0F172A at 20% opacity) to prevent a "floating" look.
- **Interactive States:** Use a primary color glow (2px outer stroke) rather than heavy shadows to indicate focus, maintaining the "hardened" geometric aesthetic.

## Shapes

The shape language is disciplined and geometric. While the system uses a "Soft" base, specific radii are assigned by component scale:

- **Small (6px):** Checkboxes, tags, and small tooltips.
- **Medium (8px):** Primary buttons, input fields, and standard controls.
- **Large (12px):** Dashboard cards, modal containers, and main content areas.

This tiered approach ensures that smaller elements feel precise and sharp, while larger containers feel modern and approachable.

## Components

**Buttons:**
- Primary: Solid Indigo with white/high-contrast text. 8px radius.
- Secondary: Outline (1px) with a subtle hover fill.
- Ghost: Used for low-priority actions in toolbars.

**Input Fields:**
- Default state: Level 1 surface, 1px neutral border.
- Focus state: Primary color border (2px) with no glow, or a very tight 2px spread glow.
- Labels: Always positioned above the field, using `body-sm` font.

**Cards:**
- 12px radius. Should use a subtle header divider (1px) to separate titles from content. 
- In Dark Mode, cards should have a subtle top-light highlight (0.5px white stroke at 5% opacity) to define the edge against the background.

**Status Chips:**
- Pill-shaped (fully rounded).
- Use a "Soft Palette" (10% opacity background of the semantic color with 100% opacity text).

**Data Tables:**
- Essential for a control plane. Use a "Zebra" row style or thin horizontal dividers. 
- Headers must use `label-caps` for a technical feel.
- Column alignment: Numeric data is right-aligned; text data is start-aligned (Right for RTL, Left for LTR).

**Icons:**
- Use **Lucide** or a similar 2px stroke weight outline family. 
- Icons should always be accompanied by labels where possible, except in the collapsed sidebar.
