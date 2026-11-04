# Customer presentation (Sundaram Smart Production Intelligence)

Live deck (25 slides, downloadable as PowerPoint/PDF from the viewer): https://claude.ai/artifact/QKVbBWBbbWGbohg1aayJtE
All screens use **synthetic demo data** from this repository's simulator and backend; nothing here is customer data.

| Folder | Content |
|---|---|
| `slides/`, `deck.json` | The slide sources (format: 1920x1080 inline-styled sections). Image references (`/_blob/...`) point at assets stored in the deck itself |
| `img/` | The 13 graphics used: 3D line and equipment renders, dashboard mock-ups, charts, architecture |
| `source/` | Generators. `deckdata.py` runs the simulator and backend to extract real numbers; `iso.py`, `equipment.py`, `scenes.py` draw the isometric 3D models; `dash.py`, `dash2.py` draw the dashboard mock-ups from that data; `helpers.py`, `slides.py` write the slides |

To regenerate the images: install `librsvg2-bin` and `fonts-ibm-plex`, then run the scripts in `source/` in order
(`deckdata.py`, `equipment.py`, `scenes.py`, `dash.py`, `dash2.py`). Paths inside the scripts assume a `/home/claude/deck` working folder; adjust as needed.

Placeholders to fill before use: presenter name, date, contact details, the official Sundaram Technologies logo (a text wordmark is used),
provider names and SLA targets on the AMC slide (illustrative). Mobile alerts and the alert count on the executive mock-up are illustrative (roadmap).
