# ows interface rules

How screens are built. `docs/brand.md` covers identity (logo, colour, voice); this file covers layout and interaction.
Both are binding. The rules follow the "data-dense dashboard" style and the form and interaction guidelines of the
ui-ux-pro-max reference (field grouping, whitespace grouping, visible labels, inline errors, honest disabled states),
adapted to a calm developer console.

## Structure: one level of cards

- A page is a stack of **panels** (`.panel`). A panel is the only card. Never put a bordered or shaded box inside a
  panel to group things.
- Inside a panel, group with **sections**: a small heading (`h3`), whitespace, and at most a hairline divider between
  sections (`.panel-section`). Whitespace does the grouping, not borders.
- Dashed outlines, inset boxes and background tints are not grouping tools. Background tint is reserved for the panel
  footer and for read-only fields.

## Editing: the same view in read and edit

- Data a user can change is shown **as its form**, always: the same grid, the same columns, the same fields. There is
  no separate "edit mode" that swaps a table for inputs and back.
- Nothing to save means no save buttons. Changing a value reveals **Save** and **Undo** where the change happened
  (the row, or the panel footer for a whole list). Saving shows a spinner on the button, then the saved state.
- Values the user cannot change are plain text, not read-only inputs. An input says "you can type here".
- Lists of editable rows (record sets, firewall rules, reverse DNS) use one **edit grid** (`.edit-grid`): column
  headers once at the top, one row per item, row actions at the right end. Labels are not repeated per row; every
  input still carries an `aria-label`.
- Adding is the **last row of the same grid**: empty or pre-filled fields and an "Add" button, or a "+ Add …" button
  under the grid that appends a row. Never a separate box for "add".

## Buttons and states

- Buttons are enabled unless the action is impossible right now (a request is running, or the role does not allow
  it). Never ship a button that is disabled until the form is valid: it looks broken and explains nothing.
- Validate when the user acts and on blur; show the problem **next to the field** (`.field-error`) in plain words,
  not only in a banner at the top.
- A disabled control states why, nearby (hint text), unless a request is running.
- Row actions sit right-aligned in the last column (`td.actions` or the grid's action column). Destructive actions ask
  for confirmation in place ("Delete" → "Cancel / Delete").

## Density and type

- Dense, scannable: 8 / 12 / 16 px spacing steps inside panels; 20–24 px between panels.
- Labels are always visible (column header or field label). Placeholders show an example, never the label.
- Monospace only for real data: addresses, IDs, DNS values, ports. Prose and labels use the UI font.
- Numbers right-aligned in tables (`.num`), tabular figures.

## Responsive

- Edit grids collapse to one column below 860 px: each row becomes a stacked group separated by a divider, header
  labels move above each field.
- No horizontal page scroll; wide tables scroll inside `.table-wrap`.

## Checklist before shipping a screen

- [ ] Only one card level; groups inside panels use headings and whitespace.
- [ ] Read and edit look the same; no permanently disabled buttons.
- [ ] Errors next to the field that caused them.
- [ ] Every input has a visible label or column header plus an `aria-label`.
- [ ] Works at 375 px and in light and dark mode; focus is visible.
