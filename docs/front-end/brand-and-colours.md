# Brand colours and the accessible palette

This is the colour specification the front end is built from. Every colour token in the stylesheet comes from this page.

## Where the colours come from

Techno Surfaces supplied their logo but no colour codes and no brand font. Rather than pick colours by eye, we measured them from the logo file itself, `TechnoSurfacesApp/wwwroot/img/logo.jpg`.

Every solid pixel in the file was read and grouped by colour family, and the median of each group was taken. Using the median means the blended pixels along the edge of each shape do not pull the result.

| Part of the logo | Colour | Pixels measured |
|---|---|---:|
| Outer swoosh | `#A2B561` green | 1 411 |
| Middle swoosh | `#A5BBD7` light blue | 999 |
| Inner swoosh | `#03376E` navy | 314 |
| Wordmark | `#8B8B8B` grey | 205 |

The three swoosh colours are solid shapes, so those readings are reliable. The wordmark is thin text in a 200 by 200 pixel file, so its grey is less exact. If the client can supply the original artwork, these values should be checked against it.

No brand font has been supplied, so the app uses the operating system's own interface font.

## Why the Task 1 colours were replaced

The Task 1 prototype used colours chosen by eye. When we measured them against WCAG 2.1, 13 of the 15 colour pairs in that stylesheet failed the AA contrast minimum. White text on the main green button came out at 3.2 to 1, where 4.5 to 1 is required.

## The rule for using brand colour

The logo green and the logo light blue are both light colours. Neither can carry white text, and neither is dark enough to be used as text on white. So:

* The logo colours are used exactly as they are only where no text sits on them: accents, dividers and icons on the navy side menu.
* Wherever text is drawn in a brand colour, or sits on one, we use a darker shade of the same hue. It is found by scaling each colour channel down evenly until the pair passes, which keeps the hue the same.
* Navy carries most of the identity, because it is already dark enough for white text at 11.8 to 1.

## The palette

| Name | Value | Used for |
|---|---|---|
| Navy | `#03376E` | Side menu, primary buttons, links, focus ring |
| Logo green | `#A2B561` | Accents only. Never behind text, never as text |
| Logo light blue | `#A5BBD7` | Labels on the navy side menu, accents |
| Green ink | `#65713D` | Confirm buttons, the approved status, green text |
| Body text | `#16202E` | All main text |
| Secondary text | `#55606E` | Hints, field labels, secondary details |
| Page background | `#F5F7FA` | Behind the cards |
| Card background | `#FFFFFF` | Cards, tables, forms |
| Input border | `#8A8A8A` | Edges of text boxes and dropdowns |
| Error | `#B42318` on `#FEF3F2` | Errors, the unresolved price state, the expired status |

### Quote status colours

Every status is always shown with its written name as well, so the meaning never depends on colour alone.

| Status | Text | Background |
|---|---|---|
| Draft | `#4A5565` | `#EEF0F3` |
| Pending approval | `#8A5A00` | `#FEF6E7` |
| Approved | `#65713D` | `#EEF2E3` |
| Sent | `#5E6B7B` | `#E8EEF5` |
| Accepted | `#03376E` | `#E6EBF0` |
| Expired | `#B42318` | `#FEF3F2` |

## Contrast check

Measured with the WCAG 2.1 relative luminance formula. Text needs 4.5 to 1. The edges of controls and the focus ring need 3 to 1.

| Use | Text or mark | Background | Ratio | Needs | Result |
|---|---|---|---:|---:|---|
| Body text | `#16202E` | `#FFFFFF` | 16.40 | 4.5 | Pass |
| Body text on page background | `#16202E` | `#F5F7FA` | 15.28 | 4.5 | Pass |
| Secondary text | `#55606E` | `#FFFFFF` | 6.39 | 4.5 | Pass |
| Secondary text on page background | `#55606E` | `#F5F7FA` | 5.96 | 4.5 | Pass |
| Links | `#03376E` | `#FFFFFF` | 11.83 | 4.5 | Pass |
| Primary button and side menu | `#FFFFFF` | `#03376E` | 11.83 | 4.5 | Pass |
| Side menu labels | `#A5BBD7` | `#03376E` | 6.02 | 4.5 | Pass |
| Confirm button | `#FFFFFF` | `#65713D` | 5.27 | 4.5 | Pass |
| Green text | `#65713D` | `#FFFFFF` | 5.27 | 4.5 | Pass |
| Draft status | `#4A5565` | `#EEF0F3` | 6.62 | 4.5 | Pass |
| Pending approval status | `#8A5A00` | `#FEF6E7` | 5.52 | 4.5 | Pass |
| Approved status | `#65713D` | `#EEF2E3` | 4.63 | 4.5 | Pass |
| Sent status | `#5E6B7B` | `#E8EEF5` | 4.65 | 4.5 | Pass |
| Accepted status | `#03376E` | `#E6EBF0` | 9.86 | 4.5 | Pass |
| Expired status and errors | `#B42318` | `#FEF3F2` | 6.05 | 4.5 | Pass |
| Error text | `#B42318` | `#FFFFFF` | 6.57 | 4.5 | Pass |
| Input border | `#8A8A8A` | `#FFFFFF` | 3.45 | 3 | Pass |
| Focus ring | `#03376E` | `#FFFFFF` | 11.83 | 3 | Pass |
| Green accent on side menu | `#A2B561` | `#03376E` | 5.25 | 3 | Pass |

All 19 pairs pass.
