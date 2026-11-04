import json, os, re, datetime
ROOT = "/mnt/user-data/outputs/artifacts/bcd73222-8f0a-48ac-84a4-a35399c299ef/project"
os.makedirs(ROOT + "/slides", exist_ok=True)
IMG = {"hero": "bf2d37455c856c64f278151f09779bb9", "map": "1fb3807768b1b391dd5938f04038df95", "machines": "a752dbf5d244eac2ffb3e6069256a146",
       "infra": "938dca9c0878220cfbcea7dbb7f4840b", "exec": "ebd0cdd1824c4881e3103c1be1ade089", "line": "72aeeae412d2356a00a86430246a2711",
       "equip": "8440b976135470e410912204f5141db5", "truth": "1aaedbfe208cbd9292ba733cee87c8dc", "mobile": "d9241459a43e083389e1d0c4fef27d91",
       "amc": "b43f98da92034ace14830b1ff27d08b2", "waterfall": "d8ced3766791ae4884bda678f1829019", "cause": "ad6d4f8f779990b93f7294fb2bcf0c48",
       "arch": "f13e7200467bc097383a8eb7a1135fe0"}
DARK, PANEL, LINE, TXT, MUT = "#0B1220", "#111A2E", "#1F2B46", "#E5E9F2", "#9FB0CF"
LIGHT, LTXT, LMUT, LCARD, LLINE = "#F3F5F9", "#0F172A", "#475569", "#FFFFFF", "#D6DCE8"
ACC, ACCD, AMB, RED, GRN, PUR = "#38BDF8", "#0369A1", "#F59E0B", "#EF4444", "#22C55E", "#A855F7"
DISP = "font-family:'Oswald', Impact, sans-serif"
BODY = "font-family:'IBM Plex Sans', Arial, sans-serif"
ALLOWED = set("""position left top right bottom width height min-width min-height max-width max-height display flex-direction flex-wrap gap align-items justify-content
justify-items align-self justify-self flex flex-grow flex-shrink flex-basis grid-template-columns grid-template-rows grid-column grid-row aspect-ratio padding overflow
font-family font-size font-weight font-style line-height letter-spacing text-align text-transform white-space text-decoration font-variant-numeric color background
border border-top border-right border-bottom border-left border-radius box-shadow opacity transform object-fit""".split())
order, notes_all = [], {}


def guard(html):
    for m in re.finditer(r'style="([^"]*)"', html):
        for decl in m.group(1).split(";"):
            if ":" in decl:
                prop = decl.split(":")[0].strip()
                assert prop in ALLOWED, f"unsupported css property {prop} in {decl}"
    assert "class=" not in html and "var(" not in html and "<style" not in html


def wrap(sid, dark, body, note, footer="", extra_bg=None, pad="128px 128px 160px", first=False, just="flex-start"):
    n = len(order) + 1
    bg = extra_bg or (DARK if dark else LIGHT)
    fg, mu = (TXT, MUT) if dark else (LTXT, LMUT)
    head = "" if first else (
        f'<p style="position:absolute; left:128px; top:56px; width:700px; {DISP}; font-size:26px; letter-spacing:4px; font-weight:600; color:{ACC if dark else ACCD}">SUNDARAM TECHNOLOGIES</p>'
        f'<p style="position:absolute; left:1064px; top:58px; width:728px; text-align:right; font-size:24px; color:{mu}">Smart Production Intelligence</p>')
    foot = "" if first else (
        (f'<p style="position:absolute; left:128px; bottom:64px; width:1300px; font-size:24px; color:{mu}">{footer}</p>' if footer else "") +
        f'<p style="position:absolute; right:128px; bottom:64px; width:200px; text-align:right; font-size:24px; color:{mu}">{n}</p>')
    html = (f'<section id="{sid}" data-transition="fade" style="background:{bg}; color:{fg}; {BODY}; padding:{pad}; display:flex; flex-direction:column; {'justify-content:' + just + '; ' if just != 'flex-start' else ''}gap:32px">'
            f'{head}{body}{foot}<aside>{note}</aside></section>')
    guard(html)
    open(f"{ROOT}/slides/{sid}.html", "w").write(html)
    order.append(sid)


def h2(t, dark=True, size=68):
    return f'<h2 style="{DISP}; font-size:{size}px; font-weight:600; line-height:1.1; letter-spacing:1px; text-transform:uppercase; color:{TXT if dark else LTXT}">{t}</h2>'


def card(inner, dark=True, extra="", pad=32):
    bg, bd = (PANEL, f"border:1px solid {LINE}") if dark else (LCARD, f"border:1px solid {LLINE}")
    return f'<div style="background:{bg}; {bd}; border-radius:16px; padding:{pad}px; display:flex; flex-direction:column; gap:12px; {extra}">{inner}</div>'


def h3(t, color=None, size=34, dark=True):
    return f'<h3 style="{DISP}; font-size:{size}px; font-weight:600; line-height:1.15; letter-spacing:0.5px; color:{color or (TXT if dark else LTXT)}">{t}</h3>'


def p(t, size=26, color=None, dark=True, w=400, extra=""):
    return f'<p style="font-size:{size}px; font-weight:{w}; line-height:1.35; color:{color or (MUT if dark else LMUT)}; {extra}">{t}</p>'


def img(key, w, h, alt, extra="", fit="contain"):
    return f'<img src="/_blob/{IMG[key]}" alt="{alt}" style="width:{w}px; height:{h}px; object-fit:{fit}; border-radius:14px; {extra}">'


def row(inner, gap=24, extra=""):
    return f'<div style="display:flex; flex-direction:row; gap:{gap}px; {extra}">{inner}</div>'


def col(inner, gap=24, extra=""):
    return f'<div style="display:flex; flex-direction:column; gap:{gap}px; {extra}">{inner}</div>'


def chip(t, bg, fg="#04121F", size=24):
    return f'<p style="font-size:{size}px; font-weight:700; background:{bg}; color:{fg}; padding:6px 18px; border-radius:30px; letter-spacing:1px">{t}</p>'


