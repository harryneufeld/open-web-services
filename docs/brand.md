# ows brand guide

ows ("Open Web Services") is a calm, provider-neutral console with a wink: it borrows the vocabulary of big cloud
consoles and makes fun of it a little. The interface stays serious where it matters.

## Logo

- The word **ows**, lowercase, set in Segoe UI Variable Display, weight 800, letter-spacing −4 at 70 units, with a
  short, thick, symmetric smile centred under it: rounded ends, no arrowhead, narrower than the word.
- The word is centred optically on the smile (ink centre, not text-box centre). The artwork is cut to the ink plus a
  2-unit margin. Source of truth: `src/OpenWebServices.Web/Components/Shared/BrandMark.razor`.
- Clear space around the logo: at least the height of the smile on every side.

| Variant | Background | Word | Smile |
|---|---|---|---|
| On light | white / paper | ink `#161A22` | ows blue `#2A63D8` |
| On dark | ink `#161A22` | white | light blue `#7FA6FF` |
| App icon, favicon | ink `#161A22`, rounded square | white | light blue `#7FA6FF` |

Don't: recolour the smile in other hues, add an arrow or point to it, skew or stretch the word, put the logo on
busy imagery, or use another company's logo elements next to it.

## Colour

| Token | Value | Use |
|---|---|---|
| ows blue | `#2A63D8` | primary actions, links, focus, logo smile on light |
| Ink | `#161A22` | text, dark surfaces, icon background |
| Paper | `#F6F7F9` | page background, sidebars |
| Light blue | `#7FA6FF` | logo smile on dark |

State colours (green running, red failed, amber warning) carry meaning only and are never decoration. The UI uses one
accent; light and dark mode follow the operating system.

## Services

Every service has a playful name, a badge colour and always a plain label next to it.

| Badge | Name | Plain label | Tagline |
|---|---|---|---|
| OC2 | OC2 | Servers | Ordinary Compute Cloud. Servers at any provider you connect. |
| 66 | Route 66 | DNS | DNS that takes you where you're going. Get your kicks on port 53. |
| IOU | IOU | Provider groups | Identity of Owners & Users. Who's paying for what, honestly. |
| S4 | S4 | Storage (not built yet) | Simply Some Storage Service. One more S than you'd expect. |

## Type

System UI sans for everything (no web fonts, so the console loads fast on small hardware). Monospace only for real
data: IP addresses, IDs, DNS values.

## Voice

- Jokes go in service names, taglines, empty states and fine print.
- Buttons and labels are plain: "Add record set", "Delete server".
- Errors, confirmations and anything about money are never funny. Errors say what went wrong and how to fix it.
- Never name or allude to other companies in jokes.
