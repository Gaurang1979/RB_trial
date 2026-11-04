from helpers import *

# ---------------- 1 cover ----------------
wrap("cover", True,
     f'<img src="/_blob/{IMG["hero"]}" alt="Isometric 3D view of a production line with live status callouts" style="position:absolute; left:0px; top:0px; width:1920px; height:1080px; object-fit:cover">'
     f'<div style="position:absolute; left:0px; top:0px; width:1000px; height:1080px; background:linear-gradient(to right, rgba(11,18,32,0.97), rgba(11,18,32,0.9) 60%, rgba(11,18,32,0))"></div>'
     f'<p style="width:720px; {DISP}; font-size:30px; letter-spacing:6px; font-weight:600; color:{ACC}">SUNDARAM TECHNOLOGIES</p>'
     f'<h1 style="width:720px; {DISP}; font-size:80px; font-weight:700; line-height:1.05; text-transform:uppercase; color:{TXT}">Know what is happening on every production line</h1>'
     f'<p style="width:720px; font-size:34px; line-height:1.35; color:{MUT}">Smart Production Intelligence: from machine signals to the digital truth of production.</p>'
     f'<p style="width:720px; {DISP}; font-size:30px; letter-spacing:3px; color:{ACC}">MEASURE  →  MONITOR  →  ANALYSE  →  IMPROVE</p>'
     f'<p style="position:absolute; left:128px; bottom:64px; width:1000px; font-size:24px; color:{MUT}">Customer presentation  ·  [Presenter name]  ·  [Date]</p>',
     "Open with the picture: a line where one press has stopped and the machines behind it are starving. The live screen shows exactly this. Every screen in this deck uses synthetic demo data from our simulator, not customer data.",
     first=True, pad="128px 128px 160px", just="center")

# ---------------- 2 states ----------------
def state_card(name, mean, color):
    return f'<div style="background:{LCARD}; border-left:10px solid {color}; border-radius:16px; padding:30px; display:flex; flex-direction:column; gap:12px; flex:1">{h3(name, color, 34, False)}{p(mean, 26, LMUT, False)}</div>'

wrap("states", False,
     h2("Machine ON ≠ machine producing", False, 76) +
     p("A powered machine can be idle, waiting, blocked or bypassed. The platform tells them apart.", 32, LMUT, False) +
     row(state_card("POWERED ON", "Power is on. On its own this says nothing about production.", "#64748B") +
         state_card("RUNNING", "The machine reports a running signal.", ACCD) +
         state_card("PRODUCING", "Cycles are completed and output is counted.", "#15803D")) +
     row(state_card("STARVED", "Stopped because the upstream machine has no material to give.", "#B45309") +
         state_card("BLOCKED", "Stopped because the downstream machine cannot accept output.", "#C2410C") +
         state_card("BYPASS SUSPECTED", "Running signal on, but no cycles. Reported as a signal inconsistency, with evidence.", "#7E22CE")) +
     p("Also detected: fault, idle, sensor malfunction, communication loss, no data from a gateway.", 28, LTXT, False, 500),
     "This is the core idea. Ask the room how many of their machines show green on a screen while producing nothing. Bypass is always reported neutrally as a signal inconsistency, never as an accusation.")

# ---------------- 3 questions ----------------
qs = ["What is happening on the line right now?", "Which machines are producing, and which are not?", "Why did production stop, and for how long?", "Where is the bottleneck today?",
      "Which machine starves or blocks another?", "Is a machine bypassed, or a sensor faulty?", "Which downtime was avoidable, and what keeps failing?", "Is the equipment provider meeting its SLA?"]
def qcard(i, t):
    return card(row(f'<p style="{DISP}; font-size:44px; font-weight:600; color:{ACC}; width:70px">0{i}</p>' + p(t, 30, TXT, True, 500, "flex:1"), 16), True, "flex:1", 28)
wrap("questions", True,
     h2("Eight questions every plant head asks") +
     col(row(qcard(1, qs[0]) + qcard(2, qs[1])) + row(qcard(3, qs[2]) + qcard(4, qs[3])) + row(qcard(5, qs[4]) + qcard(6, qs[5])) + row(qcard(7, qs[6]) + qcard(8, qs[7])), 20),
     "Read them out. Each question maps to a screen you will see later: live line view, downtime, bottleneck, digital truth, AMC and SLA evidence.")

# ---------------- 4 positioning ----------------
def step(n, t, d):
    return f'<div style="flex:1; background:{DARK}; border-radius:16px; padding:32px; display:flex; flex-direction:column; gap:10px"><p style="{DISP}; font-size:56px; font-weight:600; color:{ACC}">{n}</p><h3 style="{DISP}; font-size:40px; font-weight:600; letter-spacing:1px; color:{TXT}">{t}</h3><p style="font-size:26px; line-height:1.35; color:{MUT}">{d}</p></div>'
wrap("positioning", True,
     f'<h1 style="{DISP}; font-size:112px; font-weight:700; line-height:1.05; text-transform:uppercase; color:{DARK}">Not an IoT dashboard. A production intelligence system.</h1>'
     + row(step("01", "MEASURE", "Capture real machine signals: cycles, counts, faults, queues, sensors.") + step("02", "MONITOR", "See live status, queues and the line constraint.") +
           step("03", "ANALYSE", "Find why production is lost, with evidence.") + step("04", "IMPROVE", "Act early, before small losses become large ones."), 24),
     "Positioning. Competitors show machine status. We show actual production, why it is lost and where to act. The four steps are the maturity path we take a plant through.",
     extra_bg=ACC)

# ---------------- 5 hierarchy ----------------
def hbox(t, ex, color):
    return f'<div style="flex:1; background:{PANEL}; border:1px solid {LINE}; border-radius:16px; padding:18px; display:flex; flex-direction:column; gap:10px"><h3 style="{DISP}; font-size:28px; font-weight:600; letter-spacing:0px; color:{color}">{t}</h3><p style="font-size:25px; line-height:1.3; color:{TXT}">{ex}</p></div>'
arrow = f'<p style="font-size:36px; color:{MUT}; align-self:center">→</p>'
wrap("hierarchy", True,
     h2("Your plant, modelled the way it runs") +
     row(hbox("ORGANISATION", "Corporate group", ACC) + arrow + hbox("PLANT", "Vadodara Plant", ACC) + arrow + hbox("AREA", "Assembly", ACC) + arrow +
         hbox("LINE", "Line 01", ACC) + arrow + hbox("EQUIPMENT", "PRESS-01", ACC) + arrow + hbox("SIGNAL", "Cycle, fault, count, temp.", ACC), 10) +
     row(card(h3("Multi-plant by design") + p("Compare plants and lines with one definition of every KPI.", 26), True, "flex:1") +
         card(h3("Roles and permissions") + p("Access per plant, area and line: corporate, plant, production, maintenance, quality, IT.", 26), True, "flex:1") +
         card(h3("Configuration, not code") + p("Plants, lines, equipment, signals, cycle times and thresholds are set up, not programmed.", 26), True, "flex:1")),
     "Everything in the product hangs off this hierarchy. The example is our demo factory. In your pilot, we build this tree together with your engineers during the plant survey.")

# ---------------- 6 line3d ----------------
wrap("line3d", True,
     h2("One line mapped: equipment, queues, signals", True, 60) +
     img("map", 1400, 640, "Isometric 3D map of a line: two presses, an assembly cell and a test station connected by queues, each tagged with the number of mapped signals", "align-self:center"),
     "This is the digital production line. Equipment codes, queue names and signal counts are shown in place. Queues carry a level sensor, which lets the platform tell starvation from blocking.",
     footer="3D illustration of the demo line (synthetic data)")

# ---------------- 7 signalmap ----------------
def tr(cells, head=False):
    tag = "th" if head else "td"
    return "<tr>" + "".join(f"<{tag}>{c}</{tag}>" for c in cells) + "</tr>"
rows = [("PRESS-01", "20 s", "Machine ON, cycle, fault, output count, temperature, vibration", "QUEUE-A"),
        ("PRESS-02", "30 s", "Machine ON, cycle, fault, output count, temperature, vibration", "QUEUE-B"),
        ("ASSEMBLY-01", "24 s", "Machine ON, cycle, fault, output count, temperature", "QUEUE-C"),
        ("TEST-01", "22 s", "Machine ON, cycle, fault, output count, temperature", "line output")]
tbl = (f'<table style="font-size:26px; color:{LTXT}; font-family:\'IBM Plex Sans\', Arial, sans-serif">'
       '<tr><th style="width:20%">Equipment</th><th style="width:10%">Cycle</th><th style="width:50%">Signals captured</th><th style="width:20%">Queue after</th></tr>' +
       "".join("<tr>" + "".join(f"<td>{c}</td>" for c in r) + "</tr>" for r in rows) + "</table>")
wrap("signalmap", False,
     h2("Signal mapping for Line 01", False) + tbl +
     p("Typical protocols: digital I/O, Modbus TCP and RTU, OPC-UA, MQTT, HTTP. Adapters are configured per site.", 28, LMUT, False),
     "This table is the deliverable of the plant survey: every machine, its standard cycle time, and which signals we will capture. Protocols depend on your PLCs. Many old machines only need a cycle pulse and a counter.",
     footer="Demo line values; your line is mapped during the survey")

# ---------------- 8 equip3d ----------------
def cap(t, d):
    return f'<div style="width:400px; display:flex; flex-direction:column; gap:8px">{h3(t, ACC, 32)}{p(d, 24)}</div>'
wrap("equip3d", True,
     h2("3D equipment models") +
     img("machines", 1663, 300, "Four 3D renders: hydraulic press, CNC machining centre, assembly cell with robot, test station", "", "cover") +
     row(cap("Hydraulic press", "Cycle from ram position. State from stack light. Temperature and vibration.") + cap("CNC machining centre", "Cycle and spindle signals, door, fault, temperature, vibration.") +
         cap("Assembly cell", "Cycle from station sensors, part count, fault and safety stop.") + cap("Test station", "Pass and reject result, cycle time, fault."), 21),
     "The models are used in the digital line and in the dashboards so operators recognise their own equipment. Signals shown are typical; we confirm them for each machine in the survey.",
     footer="Typical signals, confirmed per machine during the plant survey")

# ---------------- 9 infra3d ----------------
def cap3(t, d):
    return f'<div style="width:544px; display:flex; flex-direction:column; gap:8px">{h3(t, ACC, 32)}{p(d, 24)}</div>'
wrap("infra3d", True,
     h2("Queues, edge gateway and sensors") +
     img("infra", 1664, 408, "3D renders of a buffer conveyor with parts, an edge gateway next to a Raspberry Pi board, and three sensors", "", "cover") +
     row(cap3("Queue and buffer", "A level sensor shows queue size, which separates starved from blocked.") + cap3("Edge gateway", "Raspberry Pi or industrial PC. Buffers data, works offline, syncs in order.") +
         cap3("Sensors", "Proximity, temperature and vibration. Low cost, retrofit on older machines."), 16),
     "The gateway is hardware-agnostic: Raspberry Pi for pilots, industrial PC or ARM gateway for production. The bill of materials is configurable per site.")

# ---------------- dashboard slides ----------------
def dash_slide(sid, title, key, alt, items, note, foot, iw=1194, ih=672):
    txt = col("".join(col(h3(a, ACC, 34) + p(b, 26), 8) for a, b in items), 34, "flex:1")
    wrap(sid, True, h2(title, True, 60) + row(img(key, iw, ih, alt, "", "contain") + txt, 30), note, footer=foot)

dash_slide("exec", "Executive view: every plant on one screen", "exec", "Executive dashboard with output versus target, OEE trend by plant, loss ranking and line comparison",
           [("Output vs target", "Hourly, all lines, one definition."), ("OEE by plant", "Trend and comparison."), ("Where production is lost", "Ranked by machine-hours.")],
           "For corporate management. Every number is calculated from detected states. The alert count on this mock-up is illustrative: alerts are on the roadmap.", "Illustrative screen · synthetic demo data")
dash_slide("linedash", "Line view: equipment, queues, constraint", "line", "Line dashboard showing four machines with states, queues between them and an attention panel for a stopped press",
           [("Live state", "Each machine, since when."), ("Queues", "Empty or full queues explain starving and blocking."), ("Needs attention", "Why, effect, what to check, estimated loss.")],
           "This screen is rendered from the live product output at the moment Press 01 had stopped for six minutes. Note Press 02 and Assembly 01 starved behind it. The estimate is up to 18 units at standard cycle time.", "Rendered from the live read model · synthetic demo data")
dash_slide("equipdash", "Equipment view: states, cycles, evidence", "equip", "Equipment dashboard for one press with state timeline, cycle time chart, temperature trace and downtime table",
           [("Detected states", "More than ON and OFF."), ("Cycle time", "Against the standard."), ("Downtime events", "Category and evidence, cause not guessed.")],
           "For maintenance and production managers. Note the temperature trace: flat for an hour while the machine works. That is the sensor malfunction pattern shown on the next slide.", "Illustrative screen · synthetic demo data")

# ---------------- truth ----------------
wrap("truth", True,
     h2("Digital truth: powered on is not producing", True, 62) +
     img("truth", 1420, 675, "Left: running signal stays on while no cycles are completed. Right: temperature stays constant for an hour while vibration changes", "align-self:center"),
     "Left: the machine reports running for the whole period but completes no cycles for fifteen minutes. The platform reports a bypass suspicion with evidence and confidence. Right: a temperature sensor that never moves while vibration does. We flag the sensor as unhealthy instead of trusting it. Sensor-health detectors are on the roadmap; the pattern is already visible in the data.",
     footer="Real signals from the demo factory (synthetic data)")

# ---------------- cause ----------------
wrap("cause", True,
     h2("How one stop spreads down the line") +
     img("cause", 1664, 666, "Timeline: Press 01 faults; queue A drains; Press 02 and then Assembly 01 starve, shown as coloured state bands with queue levels below", "", "contain"),
     "One fault at Press 01 at 06:30. Queue A drains, then Press 02 starves, then Assembly starves. Without line relationships this looks like three separate problems. With them it is one root cause.",
     footer="Real states and queue levels from the demo factory (synthetic data)")

# ---------------- loss ----------------
def bullet(a, b):
    return col(h3(a, ACC, 34) + p(b, 26), 6)
wrap("loss", True,
     h2("From planned time to OEE: see every loss") +
     row(img("waterfall", 1000, 467, "Waterfall from planned time through fault, bypass, starved and blocked losses, speed and quality loss to OEE", "", "contain") +
         col(bullet("Availability", "Fault, bypass, starved, blocked.") + bullet("Speed", "Cycle time against standard.") + bullet("Quality", "Rejects and scrap.") + bullet("Drill down", "Machine, event, duration."), 26, "flex:1"), 40),
     "OEE is never shown alone. The losses behind it are the point. Availability is producing time over planned time; starved and blocked time are counted as losses with their own categories so they are visible, not hidden.",
     footer="One press over 24 hours · synthetic demo data")

# ---------------- mobile ----------------
wrap("mobile", True,
     h2("Plant manager alerts on the phone") +
     row(col(col(h3("What stopped, where, for how long", ACC, 34) + p("Line, machine, duration, reason.", 26), 6) +
             col(h3("Why and how much", ACC, 34) + p("Evidence, effect on other machines and estimated loss.", 26), 6) +
             col(h3("What to do next", ACC, 34) + p("View line, acknowledge, call maintenance, history.", 26), 6) +
             f'<div style="align-self:flex-start">' + chip("MOCK-UP · ON THE ROADMAP", AMB) + "</div>", 30, "flex:1") +
         img("mobile", 880, 671, "Two phone screens: a lock-screen alert that Line 01 stopped and an alert detail with action buttons", "", "contain"), 48),
     "Mobile alerts are designed, not built yet. The alert content is produced today by the live engine. The push channel and acknowledgement flow are in the next milestone.",
     footer="Mock-up · synthetic demo data")

# ---------------- amc ----------------
wrap("amc", True,
     h2("AMC and SLA evidence, not opinions") +
     row(img("amc", 1240, 620, "Repair performance by equipment provider with breakdowns, downtime, mean time to repair and SLA compliance", "", "contain") +
         col(bullet("Facts per provider", "Breakdowns, downtime, MTTR, MTBF.") + bullet("Against contract", "SLA targets are inputs.") + bullet("For reviews", "Renewals and vendor meetings. No subjective ratings."), 30, "flex:1"), 24),
     "Provider names are placeholders. The numbers come from detected fault intervals. Response time needs a maintenance acknowledgement, which arrives with the alert workflow.",
     footer="Illustrative screen · provider names and SLA targets are placeholders")

# ---------------- architecture ----------------
wrap("architecture", True,
     h2("Architecture: from machine signal to decision", True, 64) +
     img("arch", 1664, 640, "Six-step flow: machines and sensors, edge gateway, message broker, production intelligence, data store, API and apps", "", "contain"),
     "Walk left to right. Two points matter for engineers: the gateway keeps working offline and synchronises in order, and the broker keeps events if the backend is down. Both are tested.",
     footer="Protocol adapters and hardening are completed per site during the pilot")

# ---------------- security ----------------
def sec(icon, t, d):
    return card(f'<x-icon name="{icon}" style="color:{ACC}; width:48px; height:48px"></x-icon>' + h3(t, TXT, 32) + p(d, 24), True, "flex:1", 28)
wrap("security", True,
     h2("Security designed in from the start") +
     row(sec("Lock", "Encrypted transport", "TLS between gateway, broker and applications.") + sec("Key", "Device authentication", "Every gateway proves its identity.") + sec("Users", "Role-based access", "Per plant, area and line.")) +
     row(sec("Book", "Audit trail", "Who changed what, and when.") + sec("Trust", "PLCs stay isolated", "Never exposed to the internet.") + sec("Settings", "Least privilege", "Secrets managed, permissions minimal.")) +
     f'<div style="background:#2A1F05; border:1px solid #92610A; border-radius:14px; padding:22px 28px"><p style="font-size:26px; line-height:1.35; color:#FCD34D">Status today: shared API key and TLS through a reverse proxy. User accounts, roles and device certificates are completed before any pilot goes live.</p></div>',
     "Be direct about status. The design is complete; parts are built. We do not go live on a customer network before accounts, roles and device certificates are done.")

# ---------------- roles ----------------
def role(t, d):
    return card(h3(t, ACC, 30) + p(d, 24), True, "flex:1", 26)
wrap("roles", True,
     h2("One platform, seven roles") +
     row(role("Corporate management", "Plant comparison, trends, OEE, major losses.") + role("Plant manager", "Live plant and line status, target vs actual, alerts.") +
         role("Production manager", "Line and machine performance, queues, shifts.") + role("Maintenance manager", "Failures, repeat faults, sensor health, evidence.")) +
     row(role("Quality manager", "Rejects, scrap, quality-related stops.") + role("IT and OT team", "Gateway health, connectivity, data flow, logs.") +
         role("System administrator", "Users, roles, plants, lines, equipment, thresholds.") + role("Factory display", "Line status, output, target, OEE and alerts on a large screen.")),
     "Every role sees the same facts at the level they need. The factory display is a large-screen view for the shop floor.")

# ---------------- value ----------------
vt = (f'<table style="font-size:30px; color:{LTXT}; font-family:\'IBM Plex Sans\', Arial, sans-serif"><tr><th style="width:42%">Today</th><th style="width:58%">With Smart Production Intelligence</th></tr>' +
      "".join(f"<tr><td>{a}</td><td>{b}</td></tr>" for a, b in [
          ("Machine status only", "Actual production, cycle by cycle"),
          ("Downtime reasons unknown or remembered", "Every stop detected, timed and categorised"),
          ("Bottleneck guessed", "Constraint found from starvation and blocking"),
          ("Bypass and dead sensors invisible", "Signal inconsistencies flagged with evidence"),
          ("Vendor talks from memory", "Factual MTBF, MTTR and SLA evidence")]) + "</table>")
wrap("value", False, h2("What changes on the shop floor", False) + vt,
     "We do not quote improvement percentages: they depend on the plant. In the pilot we measure your baseline first and report the change against it.")

# ---------------- status ----------------
def blist(items, dark=False):
    return '<ul style="font-size:26px; line-height:1.55; color:%s">' % (LTXT if not dark else TXT) + "".join(f"<li>{i}</li>" for i in items) + "</ul>"
wrap("status", False,
     h2("What exists today, and what comes next", False) +
     row(card(h3("Built and tested", "#15803D", 34, False) + blist(["Factory simulator: 10 machines, 3 lines, 2 plants", "Ingestion with de-duplication, ordering and outage recovery",
                                                                        "States: producing, starved, blocked, fault, bypass suspected, no data", "KPIs: OEE, output vs target, downtime, bottleneck",
                                                                        "MQTT, PostgreSQL, live API and dashboard", "75 automated tests"]), False, "flex:1") +
         card(h3("Roadmap", ACCD, 34, False) + blist(["Sensor-health detectors and alert engine", "Mobile push notifications", "User accounts, roles, device certificates",
                                                       "Protocol adapters on real hardware", "AI insights, AMC and SLA workflow, shifts"]), False, "flex:1")) +
     f'<div style="background:#E0F2FE; border-radius:14px; padding:22px 28px"><p style="font-size:26px; line-height:1.35; color:{LTXT}">Accuracy so far is measured against simulated data (99.9% agreement). It is validated on your plant data during the pilot, and we make no accuracy claim before that.</p></div>',
     "This slide builds trust. Show what is real, what is next and how accuracy will be proven on their data. Do not oversell.")

# ---------------- pilot ----------------
def pstep(n, t, d):
    return card(f'<p style="{DISP}; font-size:52px; font-weight:600; color:{ACC}">{n}</p>' + h3(t, TXT, 32) + p(d, 24), True, "flex:1", 26)
wrap("pilot", True,
     h2("Pilot roadmap: from survey to rollout") +
     row(pstep("1", "Plant survey", "Machines, signals, network.") + pstep("2", "Signal mapping", "Sensors and gateway design.") + pstep("3", "Install", "Gateway, network, software.") + pstep("4", "Simulate", "Dry run before commissioning.")) +
     row(pstep("5", "Commission", "Real signals, counts, downtime.") + pstep("6", "Validate KPIs", "Reconcile with plant records.") + pstep("7", "Train", "Dashboards, alerts, users.") + pstep("8", "Rollout", "Pilot line, then more lines and plants.")),
     "Durations depend on the plant and are agreed after the survey. The validation step is where accuracy is proven against your own records.",
     footer="Scope and durations agreed per plant")

# ---------------- commercial ----------------
def com(t, d):
    return card(h3(t, ACCD, 34, False) + p(d, 26, LMUT, False), False, "flex:1", 30)
wrap("commercial", False,
     h2("Commercial model", False) +
     row(com("Hardware", "Gateway, enclosure, power, UPS, I/O and sensors as a configurable bill of materials.") + com("Software", "Licence or subscription per plant and line.") + com("Implementation", "Survey, mapping, installation, commissioning, training.")) +
     row(com("Support and AMC", "Ongoing support with agreed service levels.") + com("Analytics and AI", "Evidence-based insights on your data.") + com("Enterprise", "Multi-plant roll-out and corporate views.")),
     "Pricing is proposed after the plant survey. Do not quote numbers from this deck.",
     footer="Pricing proposed after the plant survey")

# ---------------- next ----------------
wrap("next", True,
     f'<img src="/_blob/{IMG["hero"]}" alt="" style="position:absolute; left:0px; top:0px; width:1920px; height:1080px; object-fit:cover; opacity:0.28">'
     f'<h1 style="{DISP}; font-size:112px; font-weight:700; line-height:1.05; text-transform:uppercase; color:{TXT}">Let us map your first line</h1>' +
     row(card(f'<p style="{DISP}; font-size:52px; color:{ACC}">1</p>' + h3("Walk the line", TXT, 34) + p("Choose one line and list its machines.", 26), True, "flex:1") +
         card(f'<p style="{DISP}; font-size:52px; color:{ACC}">2</p>' + h3("Agree the pilot", TXT, 34) + p("Scope, success criteria and baseline.", 26), True, "flex:1") +
         card(f'<p style="{DISP}; font-size:52px; color:{ACC}">3</p>' + h3("Go live", TXT, 34) + p("Gateway on one line, first live dashboard.", 26), True, "flex:1"), 24) +
     f'<p style="font-size:30px; line-height:1.5; color:{TXT}">[Presenter name]  ·  [email]  ·  [phone]<br>Sundaram Technologies  ·  [address / website]</p>',
     "Close with the ask: one line, one survey, one pilot. Fill in the presenter and contact details before use.")

# ---------------- deck.json ----------------
D = {"v": 4, "createdOnFiles": {"v": 1, "at": datetime.datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%SZ")},
     "title": "Sundaram Smart Production Intelligence – Customer Presentation", "order": order,
     "sections": {"s1": {"description": "Introduction: the promise", "start": "cover"},
                  "s2": {"description": "The problem: machine ON is not machine producing", "start": "states"},
                  "s3": {"description": "The approach: model, map and see the line", "start": "positioning"},
                  "s4": {"description": "The product: equipment, dashboards, digital truth, losses, mobile, AMC", "start": "equip3d"},
                  "s5": {"description": "How it works: architecture, security, roles", "start": "architecture"},
                  "s6": {"description": "Proof, pilot and commercial model", "start": "value"},
                  "s7": {"description": "Next steps", "start": "next"}},
     "faces": {"oswald": {"family": "Oswald", "href": "https://fonts.googleapis.com/css2?family=Oswald:wght@500;600;700&display=swap"},
               "ibm-plex-sans": {"family": "IBM Plex Sans", "href": "https://fonts.googleapis.com/css2?family=IBM+Plex+Sans:wght@400;500;600;700&display=swap"}},
     "designSystems": []}
json.dump(D, open(ROOT + "/deck.json", "w"), indent=1)
print(len(order), "slides:", order)
