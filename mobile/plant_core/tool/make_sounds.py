# Synthesises the plant app's click and alert sounds (mono, 22.05 kHz,
# 16-bit WAV). Taken from secure_me's console (apps/phone_features/tool),
# same tones, so both products sound alike.
import math, struct, sys, wave, os
R = 22050
def tone(f0, f1, dur, kind='sine', gain=.5, delay=0.0, attack=.004):
    n = int(R*(dur+delay)); out = [0.0]*n; ph = 0.0
    for i in range(int(R*delay), n):
        t = (i - R*delay)/R; f = f0*(f1/f0)**(t/dur)
        ph += 2*math.pi*f/R
        if kind == 'sine': v = math.sin(ph)
        elif kind == 'tri': v = 2/math.pi*math.asin(math.sin(ph))
        elif kind == 'square': v = 1.0 if math.sin(ph) >= 0 else -1.0
        else: v = 2*((ph/(2*math.pi)) % 1) - 1
        a = min(1, t/attack) * math.exp(-5*t/dur)
        out[i] = v*a*gain
    return out
def mix(*parts):
    n = max(len(p) for p in parts); o = [0.0]*n
    for p in parts:
        for i, v in enumerate(p): o[i] += v
    return o
S = {
 'tick': tone(1900, 900, .05, 'tri', .55),
 'hover': tone(2400, 2200, .03, 'sine', .25),
 'confirm': mix(tone(660, 700, .09, 'sine', .45), tone(990, 1040, .16, 'sine', .45, .07)),
 'undo': tone(880, 520, .14, 'sine', .45),
 'scan': tone(300, 1400, .24, 'saw', .16),
 'alert': mix(tone(520, 500, .1, 'square', .18), tone(520, 500, .1, 'square', .18, .15)),
}
# The three alert levels (owner decisions 2026-10-23), from the design
# pack's notes: 5 ms attack, no clip over 0.35 s. Each is scaled to a
# peak in dBFS inside the built set's range (tick -9, confirm -8,
# alert -17): calm, never louder than a tap; critical stands out by its
# three pulses, not by volume (owner: sounds must not irritate).
S['notice'] = tone(1319, 1319, .12, 'sine', 1, 0, .005)
S['attention'] = mix(tone(880, 880, .09, 'sine', 1, 0, .005), tone(1175, 1175, .1, 'sine', 1, .12, .005))
S['critical'] = mix(*[tone(1397, 1397, l, 'tri', 1, at, .005) for l, at in ((.07, 0), (.07, .12), (.09, .24))])
PEAK_DB = {'notice': -15.0, 'attention': -13.0, 'critical': -11.0}

# Usage: make_sounds.py <dir> [name ...]  (all clips, or only those named)
d = sys.argv[1]
for k, v in S.items():
    if len(sys.argv) > 2 and k not in sys.argv[2:]: continue
    if k in PEAK_DB:
        top = max(abs(x) for x in v)
        v = [x/top*10**(PEAK_DB[k]/20)*32768/32000 for x in v]
        pad = int(R*.01)
    else:
        pad = int(R*.02)
    v = v + [0.0]*pad
    with wave.open(os.path.join(d, k+'.wav'), 'wb') as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(R)
        w.writeframes(b''.join(struct.pack('<h', int(max(-1, min(1, x))*32000)) for x in v))
