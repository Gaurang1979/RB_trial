# Python copy of the queue / camera / reject logic of plc/delta-dvp/VisionLineDVP.st (encoder mode), run
# scan by scan through the multi-camera tracking scenarios. Keep it in step with the ST programs.
# Run: python3 plc/queue_sim.py
class Cam:
    def __init__(s): s.St=0; s.Trigger=False; s.TriggerId=0; s.Finished=s.Ok=s.TimedOut=s.Refused=s.NotReady=False; s.t=0
    def __call__(s, Start, Id, status, echo, scan_ms=2, delay=0):
        s.Finished=s.TimedOut=s.Refused=s.NotReady=False
        if Start:
            if s.St==0 and status&1: s.St=1; s.TriggerId=Id; s.t=0
            else: s.NotReady=True
        if s.St==1:
            s.t+=scan_ms
            if s.t>=delay: s.Trigger=True; s.St=2; s.t=0
        elif s.St==2:
            s.t+=scan_ms
            if status&4 and echo==s.TriggerId: s.Ok=status&0x18==8; s.Finished=True; s.Trigger=False; s.St=3
            elif status&0x20: s.Ok=False; s.Finished=True; s.Refused=True; s.Trigger=False; s.St=3
            elif s.t>=1000: s.Ok=False; s.Finished=True; s.TimedOut=True; s.Trigger=False; s.St=3
        elif s.St==3 and not status&0x24: s.St=0

class Vision:   # each camera answers after `lat` scans; verdict decided by a function of the part id
    # fault(c, id) -> None (normal), 'refuse' (TriggerRejected), 'silent' (never answers), 'stale' (Done
    # and OK with the echo of the part before), 'both' (Done with OK and NG set)
    def __init__(s, verdict, lat=5, fault=None):
        s.verdict=verdict; s.lat=lat; s.fault=fault or (lambda c,i: None)
        s.st=[{'done':False,'n':-1,'id':0,'ok':False,'rej':False,'both':False} for _ in range(3)]
    def step(s, cams):
        for c in range(3):
            x=s.st[c]; t=cams[c].Trigger
            if t and not x['done'] and not x['rej']:
                if x['n']<0:
                    f=s.fault(c,cams[c].TriggerId)
                    if f=='refuse': x['rej']=True; continue          # echo stays the part before
                    x['n']=10**9 if f=='silent' else s.lat; x['both']=f=='both'
                    x['id']=cams[c].TriggerId-1 if f=='stale' else cams[c].TriggerId
                    x['stale']=f=='stale'
                x['n']-=1
                if x['n']==0: x['done']=True; x['ok']=True if x['stale'] else s.verdict(c,x['id'])
            if not t: x['done']=False; x['n']=-1; x['rej']=False
    def status(s,c):
        x=s.st[c]; d=x['done']
        return 1|(4 if d else 0)|(8 if d and (x['ok'] or x['both']) else 0)|(16 if d and (not x['ok'] or x['both']) else 0)|(32 if x['rej'] else 0)
    def echo(s,c): return s.st[c]['id']

def run(cameras, src, events, verdict, total_scans=4000, dist=1000, falls=None, len_nom=0, tol=20,
        min_gap=0, reject_touching=True, reject_length=True, log=None, fault=None, lat=5,
        seen=lambda i: True, verify_after=20, verify_win_ms=200, stats=None, reject_delay_ms=None):
    # events: {scan: [sensor numbers 0-2 with an edge]}; falls: {scan: True} = camera 1 sensor goes off
    # encoder advances 1 pulse per scan (2 ms); log collects (id, flags, length, gap, group) per part leaving
    # reject verification (X4): a rejected part the chute sees (seen(id)) gives a verify edge
    # verify_after scans after the reject; stats gets the rejector's 'ok' and 'miss' counts and the scans
    # the rejector fired ('fires'). reject_delay_ms: time mode, the part leaves at the reject sensor
    # (dist) and the rejector fires that long after it, each NOT OK part on its own (ring of 8)
    Q=16; QPos=[0]*Q; QId=[0]*Q; QRes=[0]*Q; QHead=QTail=QCount=0; Sta=[-1]*3; CamSlot=[0]*3; NextId=0
    QFlags=[0]*Q; QLen=[0]*Q; QGap=[0]*Q; QGroup=[1]*Q; OnStart=0; LastFall=None; OnSlot=0; OnId=0
    falls=falls or {}; SensorDist=[0]*3; SensLearn=[True]*3
    used=[True, False, False]
    for c in range(1,3):
        if c<cameras: used[src[c]]=True
    cams=[Cam() for _ in range(3)]; vis=Vision(verdict, lat, fault); rejected=[]; passed=[]; enc=0; missed=0
    prev_rej=False; beam=set(); VerT=[0]*8; VerHead=VerCount=0; rej_ok=rej_miss=0; fires=[]; RejT=[]
    for scan in range(total_scans):
        enc+=1; vis.step(cams); now=2*scan; VerAdd=0
        e=events.get(scan,[]); Edge=[False, 1 in e and used[1], 2 in e and used[2]]
        if 0 in e:
            OnStart=enc; Gap=enc-LastFall if LastFall is not None else 0
            if QCount<16:
                NextId+=1; QPos[QTail]=enc; QId[QTail]=NextId; QRes[QTail]=0
                QFlags[QTail]=0; QLen[QTail]=0; QGap[QTail]=Gap; QGroup[QTail]=1
                if min_gap and LastFall is not None and Gap<min_gap: QFlags[QTail]|=0x40
                OnSlot=QTail; OnId=NextId
                for c in (1,2):
                    if used[c] and Sta[c]<0: Sta[c]=QTail
                Edge[0]=True; Sta[0]=QTail; QTail=(QTail+1)%Q; QCount+=1
            else: OnId=0
        if falls.get(scan):
            Len=enc-OnStart; LastFall=enc
            if OnId and QId[OnSlot]==OnId:
                QLen[OnSlot]=Len
                if len_nom and Len>len_nom*(100+tol)//100:
                    n=(Len+len_nom//2)//len_nom
                    if n>=2:
                        n=min(n,8); QFlags[OnSlot]|=0x10; QGroup[OnSlot]=n; QLen[OnSlot]=Len//n
                        for g in range(1,n):
                            if QCount<16:
                                NextId+=1; QPos[QTail]=OnStart+Len*g//n; QId[QTail]=NextId; QRes[QTail]=0
                                QFlags[QTail]=0x10; QLen[QTail]=Len//n; QGap[QTail]=0; QGroup[QTail]=n|g<<4
                                for c in (1,2):
                                    if used[c] and Sta[c]<0: Sta[c]=QTail
                                QTail=(QTail+1)%Q; QCount+=1
                    else: QFlags[OnSlot]|=0x20
                elif len_nom and Len<len_nom*(100-tol)//100: QFlags[OnSlot]|=0x20
        SSlot=[-1]*3
        if Edge[0]: SSlot[0]=Sta[0]
        for c in (1,2):
            if not Edge[c]: continue
            Slot=Sta[c]
            if Slot>=0 and SensLearn[c]:
                SensorDist[c]=enc-QPos[Slot]; SensLearn[c]=False
            elif Slot>=0 and SensorDist[c]>0:
                Win=SensorDist[c]//5+20
                for _ in range(16):          # skip parts that passed this sensor unseen
                    if Slot<0 or enc-QPos[Slot]<=SensorDist[c]+Win: break
                    missed+=1; QFlags[Slot]|=0x08; k=(Slot+1)%Q; Slot=-1 if k==QTail else k
                if Slot>=0 and enc-QPos[Slot]<SensorDist[c]-Win: Slot=-2   # no part expected here yet
            if Slot==-2: missed+=1; Slot=-1           # pointer unchanged
            elif Slot<0: missed+=1; Sta[c]=-1
            else: k=(Slot+1)%Q; Sta[c]=-1 if k==QTail else k
            SSlot[c]=Slot
        for c in range(3):
            Slot=SSlot[src[c]] if c<cameras else -1
            cams[c](Slot>=0, QId[max(Slot,0)], vis.status(c), vis.echo(c))
            if Slot>=0:
                if not cams[c].NotReady: CamSlot[c]=Slot   # not inspected: no answer = NOT OK
            if cams[c].Finished:
                k=CamSlot[c]
                if QId[k]==cams[c].TriggerId and not cams[c].TimedOut and not cams[c].Refused:
                    QRes[k]|=1<<c
                    if cams[c].Ok: QRes[k]|=16<<c
        if QCount>0 and enc-QPos[QHead]>=dist:
            Slot=QHead; good=all((QRes[Slot]&(0x11<<c))==(0x11<<c) for c in range(cameras))
            if QFlags[Slot]&0x10 and (reject_touching or QGroup[Slot]&0xF0): good=False
            if QFlags[Slot]&0x20 and reject_length: good=False
            if log is not None: log.append((QId[Slot], QFlags[Slot], QLen[Slot], QGap[Slot], QGroup[Slot]))
            (passed if good else rejected).append(QId[Slot])
            if not good:
                one=not (prev_rej and QGroup[Slot]&0xF0)             # touching the reject before: one block
                if reject_delay_ms is None: VerAdd+=one; fires.append(scan)
                elif len(RejT)<8: RejT.append((now, one))
                else: rej_miss+=1
                if seen(QId[Slot]): beam.add(scan+verify_after+(reject_delay_ms or 0)//2)
            prev_rej=not good
            for c in range(3):
                if Sta[c]==Slot:
                    if c>0 and used[c]: missed+=1
                    k=(Slot+1)%Q; Sta[c]=-1 if k==QTail else k
            QId[Slot]=0; QHead=(QHead+1)%Q; QCount-=1
        if RejT and now-RejT[0][0]>=reject_delay_ms: VerAdd+=RejT.pop(0)[1]; fires.append(scan)
        # reject verification, as in the ST program: each reject needs its own X4 edge in time
        if scan in beam and VerCount>0: VerHead=(VerHead+1)%8; VerCount-=1; rej_ok+=1
        if VerCount>0 and now-VerT[VerHead]>verify_win_ms: VerHead=(VerHead+1)%8; VerCount-=1; rej_miss+=1
        for _ in range(VerAdd):
            if VerCount<8: VerT[(VerHead+VerCount)%8]=now; VerCount+=1
            else: rej_miss+=1
    if stats is not None: stats.update(ok=rej_ok, miss=rej_miss, fires=fires)
    return rejected, passed, missed

N=12
def parts(offsets, gap=150, skip=()):
    ev={}
    for i in range(N):
        for s,o in enumerate(offsets):
            if o is None or (i+1,s) in skip: continue
            ev.setdefault(i*gap+o,[]).append(s)
    return ev
# 1. front/back at one place (camera 2 on sensor 1), camera 3 at its own place 400 pulses on
r=run(3,[0,0,2],parts([0,None,400]), lambda c,i: not (c==1 and i==5))
print('front/back + own place, part 5 back NG ->', r); assert r[0]==[5] and len(r[1])==N-1
r=run(3,[0,0,2],parts([0,None,400]), lambda c,i: not (c==2 and i==8))
print('camera 3 NG on part 8 ->', r); assert r[0]==[8]
# 2. three places, camera 2 sensor misses part 4
r=run(3,[0,1,2],parts([0,200,400],skip={(4,1)}), lambda c,i: True)
print('3 places, camera 2 sensor misses part 4 ->', r); assert r[0]==[4]
# 3. all three cameras on one sensor, all OK
r=run(3,[0,0,0],parts([0]), lambda c,i: True)
print('3 cameras one place, all OK ->', r); assert r[0]==[]
# 4. parts closer than the vision answer time: camera busy -> not inspected -> rejected
r=run(2,[0,1,2],parts([0,200],gap=6), lambda c,i: True, total_scans=2000)
print('parts every 6 scans (camera busy) ->', len(r[0]),'rejected', len(r[1]),'passed'); assert r[0] and r[1]
# 5. camera 2 sensor sees an extra edge (reflection) between parts 6 and 7
ev=parts([0,200,400]); ev.setdefault(6*150+200+70,[]).append(1)
r=run(3,[0,1,2],ev, lambda c,i: not (c==1 and i==9))
print('extra edge at camera 2, part 9 NG at camera 2 ->', r); assert r[0]==[9]
# 6. two consecutive misses at camera 3
r=run(3,[0,1,2],parts([0,200,400],skip={(3,2),(4,2)}), lambda c,i: True)
print('camera 3 misses parts 3 and 4 ->', r); assert r[0]==[3,4]
# 7. length / gap / touching: products 80 long, gap 70; parts 4+5 touching (one 160 signal), part 9
#    half length (broken), part 11 only 10 gap. Camera 1 only.
ev, fa = {}, {}
x = 0
spec = {4: (160, 70), 9: (40, 70), 11: (80, 10)}
for i in range(1, 13):
    if i == 5: continue                       # part 5 is touching part 4: no signal of its own
    ln, gp = spec.get(i, (80, 70))
    x += gp; ev.setdefault(x, []).append(0); x += ln; fa[x] = True
log = []
r = run(1, [0, 1, 2], ev, lambda c, i: True, total_scans=3000, falls=fa, len_nom=80, min_gap=30, log=log)
print('touching 4+5, short 9, small gap before 11 ->', r[0])
fl = {pid: f for pid, f, *_ in log}
assert r[0] == [4, 5, 9], r[0]                 # 5 = the hidden touching product (ids follow the queue)
assert fl[4] & 0x10 and fl[5] & 0x10 and fl[9] & 0x20 and fl[11] & 0x40 and not fl[11] & 0x30
assert [g for pid, f, ln, gp, g in log if pid == 5][0] == 2 | 1 << 4
# 8. same line, touching not rejected by setting: the inspected first product passes, the hidden one is rejected
r = run(1, [0, 1, 2], ev, lambda c, i: True, total_scans=3000, falls=fa, len_nom=80, reject_touching=False)
print('touching kept when allowed ->', r[0]); assert r[0] == [5, 9]
# 9. two cameras at two places (Req 17.10 MRG-05): camera 2 refuses part 3's trigger (TriggerRejected):
#    part 3 is NG at once and camera 2 takes part 4 normally (no 1 s wait)
r=run(2,[0,1,2],parts([0,200]), lambda c,i: True, fault=lambda c,i: 'refuse' if (c,i)==(1,3) else None)
print('camera 2 refuses part 3 ->', r); assert r[0]==[3] and len(r[1])==N-1
# 10. camera 2 never answers part 6: no result in time is NG, the parts after it are judged on their own
st={}
r=run(2,[0,1,2],parts([0,200],gap=600), lambda c,i: True, fault=lambda c,i: 'silent' if (c,i)==(1,6) else None,
      total_scans=9000, dist=1400)
print('camera 2 silent on part 6 ->', r); assert r[0]==[6] and len(r[1])==N-1
# 11. camera 2 answers part 7 with the echo of part 6 (OK): not part 7's result, so part 7 is NG after
#     the 1 s timeout; parts 8-10 reach camera 2 while it still waits: not inspected, NG too
r=run(2,[0,1,2],parts([0,200]), lambda c,i: True, fault=lambda c,i: 'stale' if (c,i)==(1,7) else None)
print('camera 2 echoes part 6 for part 7 ->', r); assert r[0]==[7,8,9,10] and r[1]==[1,2,3,4,5,6,11,12]
# 12. camera 1 sets OK and NG together for part 2: NG
r=run(2,[0,1,2],parts([0,200]), lambda c,i: True, fault=lambda c,i: 'both' if (c,i)==(0,2) else None)
print('OK and NG both set on part 2 ->', r); assert r[0]==[2]
# 13. X4: parts 4 and 5 NG close together (one valve pulse), the chute sees only part 4: one miss
st={}
r=run(2,[0,1,2],parts([0,200],gap=8), lambda c,i: i not in (4,5), total_scans=3000, seen=lambda i: i!=5,
      lat=2, stats=st)
print('NG 4 and 5 close together, chute sees 4 only ->', r[0], st)
assert 4 in r[0] and 5 in r[0] and st['miss']>=1
st={}
r=run(2,[0,1,2],parts([0,200],gap=8), lambda c,i: i not in (4,5), total_scans=3000, lat=2, stats=st)
print('NG 4 and 5 close together, both seen ->', st); assert st['miss']==0 and st['ok']==len(r[0])
# 14. touching products 4+5 rejected together: the chute sees one block, one verify, no miss
st={}
r = run(1, [0, 1, 2], ev, lambda c, i: True, total_scans=3000, falls=fa, len_nom=80, seen=lambda i: i!=5, stats=st)
print('touching 4+5 rejected as one ->', r[0], st); assert st['miss']==0
# 15. time mode: parts 4 and 5 NOT OK 8 scans apart, reject delay 100 ms (50 scans): one firing each, 50
#     scans after each part reached the reject sensor (one delay timer used to fire once for both)
st={}
r=run(2,[0,1,2],parts([0,200],gap=8), lambda c,i: i not in (4,5), total_scans=3000, lat=2, stats=st,
      reject_delay_ms=100)
print('time mode, NOT OK 4 and 5 inside one delay ->', r[0], st)
assert r[0]==[4,5] and len(st['fires'])==2 and st['fires'][1]-st['fires'][0]==8 and st['miss']==0
print('all scenarios OK')
