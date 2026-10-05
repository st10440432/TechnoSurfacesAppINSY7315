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
| Sign in | Pass | Pass | Pass | Pass | Pass |
| Forgot password | Pass | Pass | Pass | Pass | Pass |
| Activate account | Pass | Pass | Pass | Pass | Pass |
| Dashboard | Pass | Pass | Pass | Pass | Pass |
| Quote list | Pass | Pass | Pass | Pass | Pass |
| New quote | Pass | Pass | Pass | Pass | Pass |
| Costing sheet | Pass | Pass | Pass | Pass | Pass |
| Customer quotation | Pass | Pass | Pass | Pass | Pass |
| Approval queue | Pass | Pass | Pass | Pass | Pass |
| Review quote | Pass | Pass | Pass | Pass | Pass |
| Version history | Pass | Pass | Pass | Pass | Pass |
| Invoice record | Pass | Pass | Pass | Pass | Pass |
| Material catalogue | Pass | Pass | Pass | Pass | Pass |
| Price editor | Pass | Pass | Pass | Pass | Pass |
| Customers | Pass | Pass | Pass | Pass | Pass |
| Customer detail | Pass | Pass | Pass | Pass | Pass |
| Rate card | Pass | Pass | Pass | Pass | Pass |
| Users | Pass | Pass | Pass | Pass | Pass |
| Quotation terms | Pass | Pass | Pass | Pass | Pass |
| Audit trail | Pass | Pass | Pass | Pass | Pass |

### How the results above were checked (5 October 2026)

Each screen was opened in Chrome at 375, 768 and 1440 pixels wide against a local copy of the app with the seeded catalogue, rate card and terms and two test quotes, one written by the Managing Director and one by an estimator. The screens behind sign in were checked as the Managing Director and again as an estimator. A screen passes a width when nothing scrolls sideways and nothing runs past the edge of the screen. Tables turn into labelled cards below 768 pixels, and the material catalogue does so below 1100 pixels as well, because it has the most columns.

At every width the same script was run in the page. It checks for exactly one main heading, headings in order, the `main` landmark, the skip link as the first thing to receive focus, a label tied to every form field, a name on every button and link, alt text on every image, no repeated ids, no `aria-describedby` or `aria-labelledby` pointing at something that is not there, and no inline styles. All twenty screens passed at all three widths.

**Keyboard.** On every screen the Tab key was pressed through the whole page with a real keyboard event, and every control that received focus was recorded. Every one showed a visible focus ring, and every visible control was reached in the order it appears. On the costing sheet the skip link was also used: Enter on it moves focus into the main content, past the menu. Disabled steps of the material choice are skipped until the step before them is chosen.

**200% zoom.** Zooming a 1440 pixel window to 200% gives the page 720 pixels to work with, so the costing sheet, customer quotation, material catalogue, rate card, new quote, users and audit trail were checked at exactly 720 pixels, and nothing scrolled sideways or was cut off. Every size in the stylesheet is in rem, so text grows with the zoom, and at 720 pixels the phone layout applies, which every screen passed at 375 pixels.

**Estimator view.** Signed in as an estimator, the side menu shows five screens and leaves out the six for the Managing Director. Opened by their address, the approval queue, price editor, rate card and quotation terms show their content read only, with a note saying only the Managing Director changes them and no form to change anything. Users and the audit trail show a page saying they are for the Managing Director, not an error. A quote written by someone else opens read only, with the reason given.

**Faults found and fixed during this pass.** The menu button showed on desktop, where the side menu is always open, because a later button rule overrode the rule hiding it. The activate account page scrolled sideways on a phone, because a long button label would not wrap. On a phone the brand panel on the sign in pages pushed the form below the bottom of the screen, so it is now a short band.

## Tool scores

Lighthouse is built into Chrome developer tools. axe DevTools is a free Chrome extension. Both are run on the three screens below, against the deployed app rather than a laptop, and every issue they report is fixed before the scores are recorded.

| Screen | Lighthouse accessibility | Lighthouse performance | axe issues |
|---|---|---|---|
| Costing sheet | Not run yet | Not run yet | Not run yet |
| Quote list | Not run yet | Not run yet | Not run yet |
| Customer quotation | Not run yet | Not run yet | Not run yet |

The customer quotation is also checked in print preview: it must fit A4, show no side menu, and contain no cost price, supplier discount or markup anywhere in the page. The page source was searched for the test quotes' cost prices, markup, discount and price origins, and none of them appear on it. The A4 print preview is still to be checked by hand.
