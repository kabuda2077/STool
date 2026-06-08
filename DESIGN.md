# STool UI/UX Design System

STool is a background Windows productivity tool for screenshot, OCR, translation, and clipboard history. The UI should feel fast, quiet, and precise: closer to PowerToys, Snipping Tool, Raycast, and Windows 11 Fluent surfaces than to a web dashboard.

## Design Principles

- **Utility first**: every screen starts with the working surface, not marketing or explanation.
- **Low interruption**: floating tools should be compact, clear, and dismissible.
- **Calm contrast**: use neutral white/gray surfaces with one blue accent for primary actions.
- **Native desktop feel**: respect Windows conventions for window chrome, focus states, menus, keyboard use, and DPI.
- **Dense but readable**: settings and history lists should scan quickly without oversized cards.

## Palette

- Primary: `#2563EB`
- Primary hover: `#1D4ED8`
- Primary active: `#1E40AF`
- Primary subtle: `#EFF6FF`
- App background: `#F7F8FA`
- Surface: `#FFFFFF`
- Surface alt: `#F1F5F9`
- Border: `#D9E0EA`
- Strong border: `#CBD5E1`
- Text primary: `#111827`
- Text secondary: `#64748B`
- Text disabled: `#94A3B8`
- Success: `#16A34A`
- Warning: `#F59E0B`
- Error: `#DC2626`

## Typography

- Font family: `Segoe UI, Microsoft YaHei UI`
- Window title: 13px, semibold
- Section title: 16-20px, semibold/bold
- Body text: 13px
- Metadata and hints: 11-12px
- Avoid viewport-scaled font sizes. Keep letter spacing at 0.

## Spacing

- Base unit: 4px
- Compact gap: 6-8px
- Form gap: 10-12px
- Panel padding: 14-18px
- Window content margin: 18-24px

## Shape And Elevation

- Buttons and inputs: 7px radius
- Panels/cards: 8px radius
- App windows: native Win11 rounded corners + drop shadow via `WindowChrome` + DWM (the shared `ModernWindow` style with `ModernWindowChrome`). Do **not** self-draw window corners or use `AllowsTransparency` for standard windows.
- Floating surfaces over `AllowsTransparency` (toast, tray menu, screenshot toolbars): rounded card 8–10px with a soft shadow. Screenshot capture overlay is the one window that genuinely needs per-pixel transparency.
- Avoid heavy shadows and nested cards.

## Components

- **PrimaryButton**: one per action group; save, translate, copy result.
- **SecondaryButton**: neutral actions; close, clear, filter.
- **DangerButton**: destructive actions; clear history, delete irreversible data.
- **IconButton**: compact toolbars only; must have tooltip.
- **Panel/Card**: use for a single grouped area, not for entire page sections inside other cards.
- **HintText**: secondary explanatory text, never bright gray hardcoded brushes.
- **Tray menu**: custom WPF popup (`TrayMenuWindow`), never a WinForms `ContextMenuStrip`. Rounded card, icon + label + right-aligned shortcut, danger styling for exit, dismiss on blur/Esc.
- **Toast**: transient non-blocking feedback (`ToastNotification`) for save/clear/validation results. Prefer over modal `MessageBox`, which is reserved for destructive confirmations.

## Screenshot UX

- Screenshot overlay is dark neutral, selection uses primary blue.
- Toolbar labels must be short and stable: `S`, `C`, `OCR`, `TR`, `PIN`, `X`.
- Pin and annotation windows own their own bitmap copy.
- All screenshot coordinates must account for per-monitor DPI.

## Clipboard UX

- History items should be compact rows with readable preview and timestamp.
- Restoring an item should not create a duplicate history entry.
- Favorite/delete controls should be understated and not emoji-based.
- Destructive clear history uses danger styling and confirmation.

## Settings UX

- Left navigation is short: General, OCR, Translation, Clipboard.
- Settings changes should take effect immediately when technically safe.
- Input validation surfaces via toast (non-blocking) or inline; never fail with parser exceptions. Reserve modal dialogs for destructive confirmations (e.g. clear history).
- Save is the one primary (blue) action per page; secondary/neutral actions use the secondary style.
- Avoid saying restart is required unless it is actually required.

## Anti-Patterns

- No decorative gradients, blobs, or marketing hero layouts.
- No emoji as primary UI icons.
- No hardcoded `Brushes.Gray`, `OrangeRed`, or random one-off colors in code-behind.
- No duplicate title bars inside a window that already has chrome.
- No large cards around every setting row.

