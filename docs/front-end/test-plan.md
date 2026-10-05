# Responsive and accessibility test plan

How the front end is checked on phones, tablets and desktops, and against accessibility needs. Results are recorded here as each screen is tested, and the tool scores are copied into the main README.

## Screen sizes

Every screen is tested at three widths, plus browser zoom.

| Width | Stands for |
|---|---|
| 375 pixels | A phone |
| 768 pixels | A tablet |
| 1440 pixels | A laptop or desktop |
| 200% zoom at 1440 pixels | A user who enlarges text |

Use the device toolbar in Chrome developer tools to set each width.

### Wide tables on small screens

Below 768 pixels, every data table turns into a stack of cards, one card per row, each value labelled. This is the same pattern for every table in the app, including the costing sheet, so a user learns it once. The alternative, sideways scrolling with a frozen first column, was not used, because on a phone it hides the totals and the error messages off the edge of the screen.

## Accessibility checks

Each of these is checked on every screen.

| Check | How to test it |
|---|---|
| Every form control has a real label tied to it, not just placeholder text | Click the label. The cursor should move into its box |
| Text contrast at least 4.5 to 1, large text at least 3 to 1 | Colours come only from the measured palette in `brand-and-colours.md` |
| Everything works with the keyboard alone, in the order it appears on screen | Put the mouse away. Use Tab, Shift and Tab, Enter and Space |
| A visible focus ring on whatever has focus | Tab through the page and watch for it |
| A skip to content link | Press Tab once on a fresh page. It should be the first thing to appear |
| Landmarks: `header`, `nav` and `main` | Check the page structure in the axe results |
| One main heading per page, headings in order | Check the heading list in the axe results |
| Totals are announced when they change | `aria-live` on the totals panel. Test with Windows Narrator |
| Errors are linked to their field | `aria-invalid` and `aria-describedby` on the field with the error |
| Meaning is never carried by colour alone | Every status shows its written name, every error shows a message |
| Nothing is lost at 200% zoom | Zoom in and read every screen |

## Results by screen

Not yet means the screen has not been tested in its final form.

| Screen | 375 | 768 | 1440 | Keyboard | 200% zoom |
|---|---|---|---|---|---|
| Sign in | Not yet | Not yet | Not yet | Not yet | Not yet |
| Forgot password | Not yet | Not yet | Not yet | Not yet | Not yet |
| Activate account | Not yet | Not yet | Not yet | Not yet | Not yet |
| Dashboard | Pass | Pass | Pass | Not yet | Not yet |
| Quote list | Pass | Pass | Pass | Not yet | Not yet |
| New quote | Pass | Pass | Pass | Not yet | Not yet |
| Costing sheet | Pass | Pass | Pass | Not yet | Not yet |
| Customer quotation | Pass | Pass | Pass | Not yet | Not yet |
| Approval queue | Pass | Pass | Pass | Not yet | Not yet |
| Review quote | Pass | Pass | Pass | Not yet | Not yet |
| Version history | Pass | Pass | Pass | Not yet | Not yet |
| Invoice record | Pass | Pass | Pass | Not yet | Not yet |
| Material catalogue | Pass | Pass | Pass | Not yet | Not yet |
| Price editor | Pass | Pass | Pass | Not yet | Not yet |
| Customers | Pass | Pass | Pass | Not yet | Not yet |
| Customer detail | Pass | Pass | Pass | Not yet | Not yet |
| Rate card | Pass | Pass | Pass | Not yet | Not yet |
| Users | Pass | Pass | Pass | Not yet | Not yet |
| Quotation terms | Pass | Pass | Pass | Not yet | Not yet |
| Audit trail | Pass | Pass | Pass | Not yet | Not yet |

### How the results above were checked (5 October 2026)

Each signed-in screen was opened in Chrome at 375, 768 and 1440 pixels wide, signed in as the Managing Director and as an estimator, against a local copy of the app with the seeded catalogue, rate card and terms. A screen passes a width when nothing scrolls sideways and nothing is cut off. Tables turn into labelled cards below 768 pixels, and the material catalogue does so below 1100 pixels as well, because it has the most columns.

The same screens were checked for structure with a script run in the page: exactly one main heading, headings in order, a label tied to every form field, a name on every button and link, alt text on every image, and no repeated ids. All seventeen passed.

Sign in, forgot password and activate account were not part of this pass. Keyboard-only use and 200% zoom have not been tested yet.

## Tool scores

Lighthouse is built into Chrome developer tools. axe DevTools is a free Chrome extension. Both are run on the three screens below, against the deployed app rather than a laptop, and every issue they report is fixed before the scores are recorded.

| Screen | Lighthouse accessibility | Lighthouse performance | axe issues |
|---|---|---|---|
| Costing sheet | Not run yet | Not run yet | Not run yet |
| Quote list | Not run yet | Not run yet | Not run yet |
| Customer quotation | Not run yet | Not run yet | Not run yet |

The customer quotation is also checked in print preview: it must fit A4, show no side menu, and contain no cost price, supplier discount or markup anywhere in the page.
