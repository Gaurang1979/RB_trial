# Plant Insight UI prototype

Clickable design of the live plant dashboard (web, PC and TV) and the
mobile app, made 2026-10-29 for [PLANT-DASHBOARD-AND-MOBILE.md](../../../docs/vision-system/PLANT-DASHBOARD-AND-MOBILE.md).

Open it: https://claude.ai/artifact/Py9Qexish3fDHqKZYDJvXr (private to the
owner until shared from its Share menu). The files here are its source,
one artboard each; `canvas.json` lays them out.

| Artboard | Who | What it shows |
| --- | --- | --- |
| Main (floor map) | Supervisor, plant manager, TV | Every line live: state by colour and shape, OEE, yield, rate, top NG; open alarms; trends that will reach a limit; AI tools |
| LineView | Supervisor, quality | Live camera images with OK/NG frame, last NG parts, shift timeline, counters, cycle time, NG reasons, station health |
| Quality | Quality, management | OEE split into availability, performance and quality; X̄ chart with Cp/Cpk and a rising-trend warning; OEE losses; defect map; lines compared |
| MobileLines | Operator, supervisor | My lines, worst first |
| MobileLine | Operator | Live picture, counters, early warning in plain words, three big actions |
| MobileAlarm | Operator | Alarm with a picture of "now" and "should look like", tap-through guided steps, read aloud, acknowledge, call supervisor |
| MobileReview | Supervisor, quality | NG review: part is good / not sure / really NG; overruled parts feed training |
| MobileSettings | Everyone | Colour mode (dark, light, phone) that recolours the screen as you tap, sounds on/off, and the three alarm sounds to try |
| MobileLinesLight, MobileLineLight | Everyone | The same screens in light mode, same colour roles |
| MobileAdmin | Plant admin | Approvals (AI model, tolerance, user), users, lines, alarm rules, licences, audit, add a line by QR code |

Sample numbers in the screens are illustrations, not measurements.
