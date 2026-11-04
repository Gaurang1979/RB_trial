(* ======================================================================================
   VisionLineDVP - Delta DVP slim (DVP12SE11T + DVP16SP11T), ISPSoft, Structured Text
   One line with 1-3 cameras at different places and ONE rejector, Vision Studio, Delta VFD:
     camera 1 trigger sensor -> the part gets a number and an encoder position (queue) ->
     each trigger sensor -> (camera delay) -> trigger of every camera on that sensor over Modbus TCP
     (camera channels) -> that camera's result is stored against the part ->
     rejector (encoder distance from camera 1, learned on the first part of every start, or
     reject sensor + delay) fires when ANY camera said NOT OK or did not inspect the part ->
     reject verification; light delay / on time; jam after the rejector stops the conveyor and
     restarts it when cleared. Cameras at the same place (front and back of the pack) share one
     sensor and inspect the same part at the same time; tower lamp, vision-ready, line-stop;
     diagnostics back to the PC. Product length and gap are measured with the encoder at the
     camera 1 sensor; one long sensor signal for several touching products (no gap) is split into
     that many parts and they are rejected with the reason; every part leaving the line is
     reported to the vision PC (part log) for the reports. Start / stop push buttons; conveyor
     motor by VFD over RS-485, VFD run terminal, direct-on-line contactor or star-delta.
   Instruments the installation does not have are bypassed in Vision Studio (admin, QA role)
   and ignored here: reject verification, jam, bin full, air, reject position sensor, E-stop.
   Interface: docs/vision-system/PLC-INTERFACE.md sections 3.1-3.5 and 4.1.
   Wiring, ISPSoft set-up, VFD ladder rungs, commissioning: docs/vision-system/PLC-DELTA-DVP.md.

   Not compiled in ISPSoft here (no ISPSoft on the development PC); plc/st_check.sh checks syntax and
   types with the open-source rusty compiler (clean, 2026-11-03). On the first ISPSoft build check:
   (1) the IEC function blocks TON / R_TRIG and user function blocks in your ISPSoft version,
   (2) the counter C251 (A/B phase on X0/X1) is started with DCNT in the first-scan rung,
   (3) the D registers of the Ethernet data exchange table below, (4) the module's I/O numbers,
   (5) TIME_TO_DINT (the ms clock of the reject verification; else TIME_TO_UDINT or a 1 ms counter).

   Ethernet data exchange (DVP12SE11T, server = vision PC, port 502, unit 1); blocks of at most
   100 words each in case the firmware limits the length:
     write D1000 x40  -> 0      every 10 ms   (input block + camera channels in, 32-39)
     read  100   x100 -> D1100  every 10 ms   (output block words 0-99)
     read  200   x44  -> D1200  every 10 ms   (output block words 100-127 + camera channels out 228-243)
     write D1040 x56  -> 40     every 100 ms  (line diagnostics, 40-95)
     write D1440 x48  -> 400    every 20 ms   (part log: a record per product leaving the line)
     read  300   x32  -> D1300  every 500 ms  (line settings, 300-331)
   VFD on COM2 (Modbus RTU, ladder rungs in the guide): D1400 command, D1401 frequency x100,
   D1410 drive status, M1410 = drive answered within 1 s.
   ====================================================================================== *)

(* One camera station: camera delay, then the four-phase handshake of its camera channel.
   Start for one scan with the part number. NotReady answers a refused Start in the same scan;
   Finished is TRUE for one scan with the result of the part in TriggerId: OK only when the vision
   PC set Done and OK (NG clear) with this part's number in the echo (Req 17.10 MRG-05). No answer in
   1 s (TimedOut) or a trigger the vision PC refused (Refused: busy / not ready, TriggerRejected) is
   NOT OK at once. The station takes a new part only after the vision PC dropped Done and
   TriggerRejected, so a bit left from the part before never answers for the next one. *)
FUNCTION_BLOCK CamStation
VAR_INPUT
    Start : BOOL;           (* sensor edge of this camera, a part is expected *)
    Id : WORD;              (* part number *)
    Delay : TIME;           (* camera delay *)
    Status : WORD;          (* channel status word from the vision PC: bit0 ready, 2 done, 3 ok, 4 ng, 5 refused *)
    IdEcho : WORD;
END_VAR
VAR_OUTPUT
    Trigger : BOOL;         (* to the channel trigger bit *)
    TriggerId : WORD;
    Active : BOOL;          (* delay running or waiting: keep the light on *)
    Finished, Ok, TimedOut, Refused : BOOL;   (* result of the part in TriggerId, one scan *)
    NotReady : BOOL;                    (* this scan's Start was refused (busy / not ready) *)
END_VAR
VAR
    St : INT;               (* 0 idle, 1 delay, 2 waiting for done, 3 waiting for done to drop *)
    tDelay, tResult : TON;
END_VAR
    Finished := FALSE; TimedOut := FALSE; Refused := FALSE; NotReady := FALSE;
    IF Start THEN
        IF St = 0 AND (Status AND 16#0001) <> 0 THEN
            St := 1; TriggerId := Id;
        ELSE
            NotReady := TRUE;                                  (* busy or not ready: not inspected *)
        END_IF;
    END_IF;
    tDelay(IN := St = 1, PT := Delay);
    IF St = 1 AND tDelay.Q THEN Trigger := TRUE; St := 2; END_IF;
    tResult(IN := St = 2, PT := T#1S);
    IF St = 2 THEN
        IF (Status AND 16#0004) <> 0 AND IdEcho = TriggerId THEN
            Ok := (Status AND 16#0018) = 16#0008; Finished := TRUE; Trigger := FALSE; St := 3;
        ELSIF (Status AND 16#0020) <> 0 THEN                   (* refused: no result will come *)
            Ok := FALSE; Finished := TRUE; Refused := TRUE; Trigger := FALSE; St := 3;
        ELSIF tResult.Q THEN
            Ok := FALSE; Finished := TRUE; TimedOut := TRUE; Trigger := FALSE; St := 3;
        END_IF;
    ELSIF St = 3 AND (Status AND 16#0024) = 0 THEN
        St := 0;
    END_IF;
    Active := St = 1 OR St = 2;
END_FUNCTION_BLOCK

PROGRAM VisionLineDVP
VAR_GLOBAL
    (* inputs: CPU X0-X7, DVP16SP11T X20-X27 *)
    i_Cam1Sensor    AT %IX2  : BOOL;   (* camera 1 trigger sensor: parts get their number here *)
    i_RejectSensor  AT %IX3  : BOOL;
    i_RejectVerify  AT %IX4  : BOOL;
    i_JamSensor     AT %IX5  : BOOL;
    i_AirOk         AT %IX6  : BOOL;
    i_EStopOk       AT %IX7  : BOOL;
    i_AutoMode      AT %IX20 : BOOL;   (* selector / start: line in automatic *)
    i_BinFull       AT %IX21 : BOOL;
    i_ResetButton   AT %IX22 : BOOL;
    i_VfdFault      AT %IX23 : BOOL;
    i_Cam2Sensor    AT %IX24 : BOOL;
    i_Cam3Sensor    AT %IX25 : BOOL;
    i_StartButton   AT %IX26 : BOOL;   (* green, NO 3-4 *)
    i_StopButton    AT %IX27 : BOOL;   (* red, NC 1-2: TRUE = not pressed *)
    (* outputs: CPU Y0-Y3, DVP16SP11T Y20-Y27 *)
    q_Rejector      AT %QX0  : BOOL;
    q_VisionLight   AT %QX1  : BOOL;
    q_VisionReady   AT %QX2  : BOOL;
    q_LineStop      AT %QX3  : BOOL;
    q_TowerGreen    AT %QX20 : BOOL;
    q_TowerAmber    AT %QX21 : BOOL;
    q_TowerRed      AT %QX22 : BOOL;
    q_Buzzer        AT %QX23 : BOOL;
    q_MotorRun      AT %QX24 : BOOL;   (* KM1 main contactor (via relay) or VFD FWD by terminal *)
    q_MotorDelta    AT %QX25 : BOOL;   (* KM2 delta contactor *)
    q_MotorStar     AT %QX26 : BOOL;   (* KM3 star contactor *)
    EncoderCount : DINT;                (* C251 current value (A/B phase X0/X1) *)
    VisIn  AT %MW1000 : ARRAY[0..39]  OF WORD;   (* 0-31 input block, 32-39 camera channels in *)
    Diag   AT %MW1040 : ARRAY[0..55]  OF WORD;
    PLog   AT %MW1440 : ARRAY[0..47]  OF WORD;   (* part log: 8 records x 6 words *)
    VisOut AT %MW1100 : ARRAY[0..143] OF WORD;   (* 0-127 output block, 128-143 camera channels out *)
    Set    AT %MW1300 : ARRAY[0..31]  OF WORD;
    VfdCmd  AT %MW1400 : WORD;          (* drive command word for the RS-485 rung: 1 stop, 2 run *)
    VfdFreq AT %MW1401 : WORD;          (* Hz x 100 *)
    VfdOnline : BOOL;                   (* M1410 from the RS-485 rung *)
END_VAR

VAR
    (* settings, taken from Set[] when its version changes (section 3.4) *)
    SetVersion : WORD;
    ConveyorHz100 : WORD := 3000;
    LightDelay, LightOn, CameraDelay, RejectDelay, RejectOn, VerifyWin, JamTime, ResumeDelay : TIME;
    RejectByEncoder : BOOL := TRUE;
    RejectDistance : DINT := 2000;     (* camera 1 sensor -> reject sensor, pulses *)
    RejectOffset : DINT;               (* reject sensor -> firing point, pulses *)
    LearnAuto : BOOL := TRUE;
    MaxConsecNg : INT := 5;
    StopOnRejectFail : BOOL := TRUE;
    Bypass : WORD;                     (* bit0 verify, 1 jam, 2 bin full, 3 air, 4 reject sensor, 5 E-stop *)
    ByVerify, ByJam, ByBin, ByAir, ByRejSensor, ByEStop : BOOL;
    Cameras : INT := 1;                (* 1-3 *)
    Src : ARRAY[0..2] OF INT;          (* trigger sensor (0-2) of each camera; own = c, shared = lower *)
    SensorUsed : ARRAY[0..2] OF BOOL;

    (* timers and edges *)
    tHb, tVisWd, tLightDelay, tLightOn, tPulse : TON;
    tJam, tResume, tStuck, tEnc, tResetOut, tMinute : TON;
    rCam1, rCam2, rCam3, rRejSens, rVerify, rReset, rJam, rEncFault, rAuto : R_TRIG;
    Cam : ARRAY[0..2] OF CamStation;

    (* vision side *)
    VisReady, VisError, VisWarning, VisionLost, VisionLostLast, ResetToVision, AnyCamActive : BOOL;
    VisHbLast, NextId, Hb : WORD;

    (* queue of parts from camera 1 to the rejector; QRes[i] bit c = camera c answered,
       bit 4+c = camera c said OK. Sta[s] = next queue slot trigger sensor s expects (in order). *)
    QPos : ARRAY[0..15] OF DINT; QId : ARRAY[0..15] OF WORD; QRes : ARRAY[0..15] OF WORD;
    QFlags : ARRAY[0..15] OF WORD;     (* reasons: 8 missed at a sensor, 16 touching, 32 length out, 64 small gap, 128 learning *)
    QLen, QGap : ARRAY[0..15] OF DINT; QGroup : ARRAY[0..15] OF WORD;   (* group size bits 0-3, index bits 4-7 *)

    (* product length and gap at the camera 1 sensor *)
    PulsesPerMetre, LenNom, LenLearnMin, MinGapP, OnStart, LastFall, Len, Gap : DINT;
    ProductLengthMm, LengthTol, LenLearnN, OnSlot, GroupN, g : INT;
    OnId, PartSeq, PartIdx, Flags, Cams : WORD;
    RejectTouching, RejectLengthOut, LastFallValid, Retrig : BOOL;
    fCam1 : F_TRIG;
    cTouching, cLengthOut, cSmallGap : UDINT;
    QHead, QTail, QCount, c, k, Slot : INT;
    Sta : ARRAY[0..2] OF INT := [-1, -1, -1];   (* per trigger sensor: queue index, -1 = none expected *)
    SSlot : ARRAY[0..2] OF INT;        (* per trigger sensor: queue slot of the part at it this scan *)
    SensorDist : ARRAY[0..2] OF DINT;  (* pulses camera 1 sensor -> sensor c, learned *)
    SensLearn : ARRAY[0..2] OF BOOL := [TRUE, TRUE, TRUE];
    Win : DINT; n : INT;
    CamSlot : ARRAY[0..2] OF INT;      (* queue slot the camera is inspecting now *)
    Edge : ARRAY[0..2] OF BOOL;
    Learning : BOOL; LearnedDistance : DINT;
    Rejecting, PartGood : BOOL;
    (* time mode: fire time due for every NOT OK part that passed the reject sensor, oldest at RejHead,
       so a second part inside one reject delay gets its own firing (requirements section 12) *)
    RejT : ARRAY[0..7] OF DINT; RejVer : ARRAY[0..7] OF BOOL; RejHead, RejCount : INT;
    (* reject verification (X4): fire time of every reject not yet seen by the verify sensor, oldest at
       VerHead; each needs its own verify edge within VerifyWin (Req 17.10 MRG-05) *)
    tClock : TON; NowMs, Age : DINT;
    VerT : ARRAY[0..7] OF DINT; VerHead, VerCount : INT; VerAdd : INT;
    PrevRejected : BOOL;               (* the part that left just before was rejected *)
    LastTrigEnc, Delta, EncLast : DINT;

    (* line state *)
    DriveType : INT;                   (* 0 VFD RS-485, 1 VFD run terminal, 2 direct-on-line, 3 star-delta *)
    StarDeltaT : TIME := T#5S; ChangeT : TIME := T#100MS;
    DriveFaultNc, DriveFault, ByStartStop, ByAuto, RunLatch : BOOL;
    SdState : INT;                     (* star-delta: 0 off, 1 star, 2 changeover, 3 delta *)
    tStar, tChange : TON; rStart : R_TRIG;
    Auto, RunEnable, Jam, Resuming, TriggerStuck, EncoderFault, AirOk, EStopOk, BinFull : BOOL;
    RejectFailAlarm, ConsecNgAlarm, TrackingAlarm, AnyFault, AnyWarning, BuzzerAck, ConveyorRun : BOOL;
    ConsecNg : INT;

    (* diagnostics *)
    cTrigSensor, cTrigSent, cResults, cMissed, cTimeouts, cRejCmd, cRejOk, cRejMiss : UDINT;
    cRejSensor, cTrackMis, cJams, cEncFaults, cVisLoss, cCam2, cCam3, cNotInspected : UDINT;
    PpMin, PpMax, StuckMsMax, PartsThisMinute, PartsPerMinute : UINT;
    StateBits : WORD;
END_VAR

(* ---------------- settings from the vision PC ---------------- *)
IF Set[0] = 1 AND Set[18] <> SetVersion THEN
    SetVersion      := Set[18];
    ConveyorHz100   := Set[1];
    LightDelay      := UINT_TO_TIME(WORD_TO_UINT(Set[2]));
    LightOn         := UINT_TO_TIME(WORD_TO_UINT(Set[3]));
    CameraDelay     := UINT_TO_TIME(WORD_TO_UINT(Set[4]));
    RejectByEncoder := Set[5] = 0;
    IF Set[14] = 0 THEN                                            (* manual distance *)
        RejectDistance := WORD_TO_DINT(Set[6]) + SHL(WORD_TO_DINT(Set[7]), 16);
    END_IF;
    RejectOffset    := INT_TO_DINT(WORD_TO_INT(Set[8]));
    RejectDelay     := UINT_TO_TIME(WORD_TO_UINT(Set[9]));
    RejectOn        := UINT_TO_TIME(WORD_TO_UINT(Set[10]));
    VerifyWin       := UINT_TO_TIME(WORD_TO_UINT(Set[11]));
    JamTime         := UINT_TO_TIME(WORD_TO_UINT(Set[12]));
    ResumeDelay     := UINT_TO_TIME(WORD_TO_UINT(Set[13]));
    LearnAuto       := Set[14] = 1;
    MaxConsecNg     := WORD_TO_INT(Set[15]);
    StopOnRejectFail := Set[16] = 1;
    IF Set[17] = 1 THEN                                            (* learn again on the next part *)
        Learning := TRUE; SensLearn[1] := TRUE; SensLearn[2] := TRUE;
    END_IF;
    Bypass          := Set[19];
    Cameras         := LIMIT(1, WORD_TO_INT(Set[20]), 3);
    ByVerify    := (Bypass AND 16#0001) <> 0;
    ByJam       := (Bypass AND 16#0002) <> 0;
    ByBin       := (Bypass AND 16#0004) <> 0;
    ByAir       := (Bypass AND 16#0008) <> 0;
    ByRejSensor := (Bypass AND 16#0010) <> 0;
    ByEStop     := (Bypass AND 16#0020) <> 0;
    ByStartStop := (Bypass AND 16#0040) <> 0;
    ByAuto      := (Bypass AND 16#0080) <> 0;
    DriveFaultNc := (Set[27] AND 16#0004) <> 0;
    DriveType   := LIMIT(0, WORD_TO_INT(Set[28]), 3);
    StarDeltaT  := UINT_TO_TIME(WORD_TO_UINT(Set[29]));
    ChangeT     := UINT_TO_TIME(WORD_TO_UINT(Set[30]));
    PulsesPerMetre  := WORD_TO_DINT(Set[22]) + SHL(WORD_TO_DINT(Set[23]), 16);
    ProductLengthMm := WORD_TO_INT(Set[24]);
    LengthTol       := LIMIT(1, WORD_TO_INT(Set[25]), 90);
    MinGapP         := WORD_TO_DINT(Set[26]) * (PulsesPerMetre / 10) / 100;
    RejectTouching  := (Set[27] AND 16#0001) <> 0;
    RejectLengthOut := (Set[27] AND 16#0002) <> 0;
    IF ProductLengthMm > 0 THEN LenNom := INT_TO_DINT(ProductLengthMm) * (PulsesPerMetre / 10) / 100; END_IF;
    Src[0] := 0;                                                   (* camera 1 always on sensor 1 *)
    Src[1] := LIMIT(1, WORD_TO_INT(Set[21] AND 16#00FF), 2) - 1;    (* byte 42 *)
    Src[2] := LIMIT(1, WORD_TO_INT(SHR(Set[21], 8)), 3) - 1;        (* byte 43 *)
    SensorUsed[0] := TRUE; SensorUsed[1] := FALSE; SensorUsed[2] := FALSE;
    FOR c := 1 TO 2 DO
        IF c < Cameras THEN SensorUsed[Src[c]] := TRUE; END_IF;
    END_FOR;
    IF ByRejSensor THEN                                            (* no sensor: fixed distance only *)
        RejectByEncoder := TRUE; LearnAuto := FALSE; Learning := FALSE;
    END_IF;
END_IF;

(* bypassed instruments read as healthy *)
AirOk   := i_AirOk OR ByAir;
EStopOk := i_EStopOk OR ByEStop;
BinFull := i_BinFull AND NOT ByBin;

(* ---------------- vision output block ---------------- *)
VisReady   := (VisOut[0] AND 16#0001) <> 0;
VisError   := (VisOut[0] AND 16#0020) <> 0;
VisWarning := (VisOut[0] AND 16#0800) <> 0;
tVisWd(IN := VisOut[2] = VisHbLast, PT := T#1S);
IF VisOut[2] <> VisHbLast THEN VisHbLast := VisOut[2]; END_IF;
VisionLost := tVisWd.Q;
IF VisionLost AND NOT VisionLostLast THEN cVisLoss := cVisLoss + 1; END_IF;
VisionLostLast := VisionLost;

tHb(IN := NOT tHb.Q, PT := T#100MS);
tClock(IN := NOT tClock.Q, PT := T#1H);                            (* ms clock, wraps every hour *)
NowMs := TIME_TO_DINT(tClock.ET);
IF tHb.Q THEN Hb := Hb + 1; END_IF;

(* ---------------- auto start: learn the distance on the first part ---------------- *)
(* drive fault / motor overload: NO contact closes on a fault, NC contact opens (and a broken wire trips) *)
DriveFault := i_VfdFault XOR DriveFaultNc;
(* start / stop buttons: Start (NO) latches, Stop (NC) or an E-stop drops it; press Start again after *)
rStart(CLK := i_StartButton);
IF ByStartStop THEN RunLatch := TRUE;
ELSIF rStart.Q AND i_StopButton AND EStopOk THEN RunLatch := TRUE;
END_IF;
IF NOT ByStartStop AND (NOT i_StopButton OR NOT EStopOk) THEN RunLatch := FALSE; END_IF;
Auto := (i_AutoMode OR ByAuto) AND RunLatch AND EStopOk AND NOT DriveFault;
rAuto(CLK := Auto);
IF rAuto.Q AND LearnAuto AND RejectByEncoder AND NOT ByRejSensor THEN Learning := TRUE; END_IF;
IF rAuto.Q AND ProductLengthMm = 0 THEN LenLearnN := 0; LenLearnMin := 0; END_IF;   (* product length: every start *)
IF rAuto.Q THEN                                                (* camera sensor positions: every start *)
    SensLearn[1] := TRUE; SensLearn[2] := TRUE;
END_IF;

(* ---------------- jam after the rejector: stop, restart when clear ---------------- *)
tJam(IN := i_JamSensor AND NOT ByJam, PT := JamTime);
rJam(CLK := tJam.Q);
IF rJam.Q THEN cJams := cJams + 1; Jam := TRUE; END_IF;
tResume(IN := Jam AND NOT (i_JamSensor AND NOT ByJam), PT := ResumeDelay);
Resuming := Jam AND NOT (i_JamSensor AND NOT ByJam);
IF tResume.Q THEN Jam := FALSE; END_IF;

tStuck(IN := i_Cam1Sensor, PT := T#3S);
TriggerStuck := tStuck.Q;
IF TIME_TO_UINT(tStuck.ET) > StuckMsMax THEN StuckMsMax := TIME_TO_UINT(tStuck.ET); END_IF;

tEnc(IN := ConveyorRun AND (EncoderCount = EncLast), PT := T#2S);
IF EncoderCount <> EncLast THEN EncLast := EncoderCount; END_IF;
rEncFault(CLK := tEnc.Q);
IF rEncFault.Q THEN cEncFaults := cEncFaults + 1; END_IF;
EncoderFault := tEnc.Q AND RejectByEncoder;

rReset(CLK := i_ResetButton);
IF rReset.Q THEN
    BuzzerAck := TRUE; RejectFailAlarm := FALSE; ConsecNgAlarm := FALSE; TrackingAlarm := FALSE;
    ConsecNg := 0; ResetToVision := TRUE;
END_IF;
tResetOut(IN := ResetToVision, PT := T#500MS);
IF tResetOut.Q THEN ResetToVision := FALSE; END_IF;

AnyFault := Jam OR TriggerStuck OR EncoderFault OR RejectFailAlarm OR ConsecNgAlarm OR TrackingAlarm
            OR VisionLost OR NOT AirOk OR BinFull OR NOT EStopOk OR DriveFault OR VisError OR (DriveType = 0 AND NOT VfdOnline);
ConveyorRun := Auto AND NOT Jam AND NOT (AnyFault AND NOT Resuming AND NOT Jam);
RunEnable := ConveyorRun AND NOT VisionLost;

(* ---------------- camera 1 sensor: a new part enters the queue ---------------- *)
rCam1(CLK := i_Cam1Sensor);
rCam2(CLK := i_Cam2Sensor AND SensorUsed[1]);
rCam3(CLK := i_Cam3Sensor AND SensorUsed[2]);
Edge[0] := FALSE; Edge[1] := rCam2.Q; Edge[2] := rCam3.Q;
IF rCam2.Q THEN cCam2 := cCam2 + 1; END_IF;
IF rCam3.Q THEN cCam3 := cCam3 + 1; END_IF;

IF rCam1.Q THEN
    cTrigSensor := cTrigSensor + 1;
    PartsThisMinute := PartsThisMinute + 1;
    Delta := EncoderCount - LastTrigEnc;
    IF cTrigSensor > 1 AND Delta > 0 AND Delta < 65535 THEN
        IF PpMin = 0 OR DINT_TO_UINT(Delta) < PpMin THEN PpMin := DINT_TO_UINT(Delta); END_IF;
        IF DINT_TO_UINT(Delta) > PpMax THEN PpMax := DINT_TO_UINT(Delta); END_IF;
    END_IF;
    LastTrigEnc := EncoderCount;
    OnStart := EncoderCount;
    Gap := 0;
    IF LastFallValid THEN Gap := EncoderCount - LastFall; END_IF;
    IF QCount < 16 THEN
        NextId := NextId + 1;
        IF NextId = 0 THEN NextId := 1; END_IF;
        QPos[QTail] := EncoderCount; QId[QTail] := NextId; QRes[QTail] := 0;
        QFlags[QTail] := 0; QLen[QTail] := 0; QGap[QTail] := Gap; QGroup[QTail] := 1;
        IF MinGapP > 0 AND LastFallValid AND Gap < MinGapP THEN
            QFlags[QTail] := QFlags[QTail] OR 16#0040; cSmallGap := cSmallGap + 1;
        END_IF;
        OnSlot := QTail; OnId := NextId;
        FOR c := 1 TO 2 DO                                         (* downstream sensors expect it *)
            IF SensorUsed[c] AND Sta[c] < 0 THEN Sta[c] := QTail; END_IF;
        END_FOR;
        Edge[0] := TRUE; Sta[0] := QTail;                          (* camera 1 inspects it now *)
        QTail := (QTail + 1) MOD 16; QCount := QCount + 1;
    ELSE
        cMissed := cMissed + 1;                                    (* queue full *)
        OnId := 0;
    END_IF;
END_IF;

(* ---------------- end of the camera 1 signal: product length, touching products ----------------
   Length = encoder pulses while the sensor is on. About n nominal lengths with no gap = n products
   touching: the first was inspected, the other n-1 are added to the queue behind it (not
   inspected) and the group is rejected. Nominal length: entered in mm, or the shortest of the
   first 5 products after every start. *)
fCam1(CLK := i_Cam1Sensor);
IF fCam1.Q THEN
    Len := EncoderCount - OnStart;
    LastFall := EncoderCount; LastFallValid := TRUE;
    IF ProductLengthMm = 0 AND LenLearnN < 5 AND Len > 0 THEN       (* learn: shortest of 5 *)
        IF LenLearnMin = 0 OR Len < LenLearnMin THEN LenLearnMin := Len; END_IF;
        LenLearnN := LenLearnN + 1;
        IF LenLearnN = 5 THEN LenNom := LenLearnMin; END_IF;
    END_IF;
    IF OnId <> 0 AND QId[OnSlot] = OnId THEN
        QLen[OnSlot] := Len;
        IF LenNom > 0 AND Len > LenNom * (100 + LengthTol) / 100 THEN
            GroupN := DINT_TO_INT((Len + LenNom / 2) / LenNom);
            IF GroupN >= 2 THEN                                     (* touching products *)
                GroupN := MIN(GroupN, 8);
                cTouching := cTouching + 1;
                QFlags[OnSlot] := QFlags[OnSlot] OR 16#0010; QGroup[OnSlot] := INT_TO_WORD(GroupN);
                QLen[OnSlot] := Len / GroupN;
                FOR g := 1 TO GroupN - 1 DO
                    IF QCount < 16 THEN
                        NextId := NextId + 1;
                        IF NextId = 0 THEN NextId := 1; END_IF;
                        QPos[QTail] := OnStart + Len * g / GroupN; QId[QTail] := NextId; QRes[QTail] := 0;
                        QFlags[QTail] := 16#0010; QLen[QTail] := Len / GroupN; QGap[QTail] := 0;
                        QGroup[QTail] := INT_TO_WORD(GroupN) OR SHL(INT_TO_WORD(g), 4);
                        FOR c := 1 TO 2 DO
                            IF SensorUsed[c] AND Sta[c] < 0 THEN Sta[c] := QTail; END_IF;
                        END_FOR;
                        QTail := (QTail + 1) MOD 16; QCount := QCount + 1;
                    ELSE
                        cMissed := cMissed + 1;
                    END_IF;
                END_FOR;
            ELSE
                QFlags[OnSlot] := QFlags[OnSlot] OR 16#0020; cLengthOut := cLengthOut + 1;   (* too long *)
            END_IF;
        ELSIF LenNom > 0 AND Len < LenNom * (100 - LengthTol) / 100 THEN
            QFlags[OnSlot] := QFlags[OnSlot] OR 16#0020; cLengthOut := cLengthOut + 1;       (* too short *)
        END_IF;
    END_IF;
END_IF;

(* ---------------- each downstream trigger sensor: find the part it sees ----------------
   Encoder mode: the sensor's distance from camera 1 is learned on the first part after every auto
   start; an edge belongs to the queued part expected there (+-20 %). Parts that passed unseen are
   skipped (no answer = rejected), edges with no part expected are ignored. So one missed or extra
   edge never shifts the results of the following parts. Time mode: parts in order. *)
SSlot[0] := -1; SSlot[1] := -1; SSlot[2] := -1;
IF Edge[0] THEN SSlot[0] := Sta[0]; END_IF;
FOR c := 1 TO 2 DO
    IF Edge[c] THEN
        Slot := Sta[c];
        IF Slot >= 0 AND SensLearn[c] THEN
            SensorDist[c] := EncoderCount - QPos[Slot]; SensLearn[c] := FALSE;
        ELSIF Slot >= 0 AND RejectByEncoder AND SensorDist[c] > 0 THEN
            Win := SensorDist[c] / 5 + 20;
            FOR n := 1 TO 16 DO                                    (* skip parts that passed unseen *)
                IF Slot < 0 THEN EXIT; END_IF;
                IF EncoderCount - QPos[Slot] <= SensorDist[c] + Win THEN EXIT; END_IF;
                cMissed := cMissed + 1; QFlags[Slot] := QFlags[Slot] OR 16#0008;
                k := (Slot + 1) MOD 16;
                IF k = QTail THEN Slot := -1; ELSE Slot := k; END_IF;
            END_FOR;
            IF Slot >= 0 THEN
                IF EncoderCount - QPos[Slot] < SensorDist[c] - Win THEN Slot := -2; END_IF;   (* none expected yet *)
            END_IF;
        END_IF;
        IF Slot = -2 THEN
            cMissed := cMissed + 1; Slot := -1;                    (* extra edge: pointer unchanged *)
        ELSIF Slot < 0 THEN
            cMissed := cMissed + 1; Sta[c] := -1;                  (* part that camera 1 never saw *)
        ELSE
            k := (Slot + 1) MOD 16;
            IF k = QTail THEN Sta[c] := -1; ELSE Sta[c] := k; END_IF;
        END_IF;
        SSlot[c] := Slot;
    END_IF;
END_FOR;

(* ---------------- each camera: triggered by its sensor, all cameras on one sensor together ---------------- *)
AnyCamActive := FALSE;
FOR c := 0 TO 2 DO
    Slot := -1;
    IF c < Cameras THEN Slot := SSlot[Src[c]]; END_IF;
    IF Slot >= 0 AND RunEnable THEN cTrigSent := cTrigSent + 1; END_IF;
    Cam[c](Start := Slot >= 0 AND RunEnable,
           Id := QId[MAX(Slot, 0)], Delay := CameraDelay,
           Status := VisOut[128 + 4 * c], IdEcho := VisOut[129 + 4 * c]);
    IF Slot >= 0 THEN
        IF NOT RunEnable OR Cam[c].NotReady THEN                   (* not inspected: no answer = NOT OK *)
            cNotInspected := cNotInspected + 1;
        ELSE
            CamSlot[c] := Slot;                                    (* this part is being inspected *)
        END_IF;
    END_IF;
    IF Cam[c].Finished THEN                                        (* result of the part in CamSlot *)
        k := CamSlot[c];
        IF QId[k] = Cam[c].TriggerId AND NOT Cam[c].TimedOut AND NOT Cam[c].Refused THEN   (* part still in the queue, answered *)
            QRes[k] := QRes[k] OR SHL(WORD#1, c);
            IF Cam[c].Ok THEN QRes[k] := QRes[k] OR SHL(WORD#16, c); END_IF;
        END_IF;
        IF Cam[c].TimedOut THEN cTimeouts := cTimeouts + 1;
        ELSIF Cam[c].Refused THEN cNotInspected := cNotInspected + 1;
        ELSE cResults := cResults + 1; END_IF;
        IF Cam[c].Ok THEN ConsecNg := 0; ELSE ConsecNg := ConsecNg + 1; END_IF;
    END_IF;
    AnyCamActive := AnyCamActive OR Cam[c].Active;
END_FOR;
IF ConsecNg >= MaxConsecNg THEN ConsecNgAlarm := TRUE; END_IF;

(* light: on after the light delay, for the light on time (0 = always on while running) *)
tLightDelay(IN := AnyCamActive, PT := LightDelay);
tLightOn(IN := tLightDelay.Q, PT := LightOn);

(* ---------------- reject station: one rejector for all cameras ---------------- *)
rRejSens(CLK := i_RejectSensor AND NOT ByRejSensor);
IF rRejSens.Q THEN
    cRejSensor := cRejSensor + 1;
    IF Learning AND QCount > 0 THEN                                 (* first part: learn the distance *)
        LearnedDistance := EncoderCount - QPos[QHead];
        QFlags[QHead] := QFlags[QHead] OR 16#0080;
        RejectDistance := LearnedDistance;
        Learning := FALSE;
    ELSIF RejectByEncoder AND (QCount = 0 OR ABS(EncoderCount - QPos[QHead] - RejectDistance) > RejectDistance / 10 + 20) THEN
        cTrackMis := cTrackMis + 1; TrackingAlarm := TRUE;
    END_IF;
END_IF;

(* the oldest part leaves the queue: at the firing point (encoder) or at the reject sensor (time) *)
Slot := -1;
IF QCount > 0 THEN
    IF RejectByEncoder THEN
        IF NOT Learning AND EncoderCount - QPos[QHead] >= RejectDistance + RejectOffset THEN Slot := QHead; END_IF;
    ELSIF rRejSens.Q THEN
        Slot := QHead;
    END_IF;
END_IF;
IF Slot >= 0 THEN
    (* good only when every camera answered and said OK; a camera that has not answered = NOT OK *)
    PartGood := TRUE;
    Flags := QFlags[Slot] AND 16#00F8; Cams := 0;
    FOR c := 0 TO 2 DO
        IF c < Cameras THEN
            IF (QRes[Slot] AND SHL(WORD#1, c)) = 0 THEN
                PartGood := FALSE; Flags := Flags OR 16#0004;      (* camera did not answer *)
            ELSIF (QRes[Slot] AND SHL(WORD#16, c)) = 0 THEN
                PartGood := FALSE; Flags := Flags OR 16#0002;      (* camera NOT OK *)
            END_IF;
        END_IF;
    END_FOR;
    IF (QFlags[Slot] AND 16#0010) <> 0 AND (RejectTouching OR (QGroup[Slot] AND 16#00F0) <> 0) THEN PartGood := FALSE; END_IF;
    IF (QFlags[Slot] AND 16#0020) <> 0 AND RejectLengthOut THEN PartGood := FALSE; END_IF;
    IF NOT PartGood THEN
        Flags := Flags OR 16#0001;
        IF RejectByEncoder THEN
            IF Rejecting THEN Retrig := TRUE; END_IF;              (* parts close together: keep the valve on *)
            (* every reject needs its own X4 edge, except a product touching the one rejected just before
               it (same group): the verify sensor sees them as one block *)
            IF NOT (PrevRejected AND (QGroup[Slot] AND 16#00F0) <> 0) THEN VerAdd := VerAdd + 1; END_IF;
            Rejecting := TRUE; cRejCmd := cRejCmd + 1;
        ELSIF RejCount < 8 THEN
            k := (RejHead + RejCount) MOD 8;
            RejT[k] := NowMs; RejVer[k] := NOT (PrevRejected AND (QGroup[Slot] AND 16#00F0) <> 0);
            RejCount := RejCount + 1;
        ELSE                                                       (* more NOT OK parts than the delay can hold *)
            cRejMiss := cRejMiss + 1; RejectFailAlarm := TRUE;
        END_IF;
    END_IF;
    PrevRejected := NOT PartGood;
    (* part log record for the vision PC *)
    PartSeq := PartSeq + 1;
    IF PartSeq = 0 THEN PartSeq := 1; END_IF;
    k := PartIdx * 6;
    PLog[k + 1] := QId[Slot];
    PLog[k + 2] := Flags;
    PLog[k + 3] := DINT_TO_WORD(LIMIT(0, QLen[Slot], 65535));
    PLog[k + 4] := DINT_TO_WORD(LIMIT(0, QGap[Slot], 65535));
    PLog[k + 5] := (QRes[Slot] AND 16#0077) OR SHL(QGroup[Slot] AND 16#00FF, 8);
    PLog[k] := PartSeq;                                            (* sequence last: record complete *)
    PartIdx := (PartIdx + 1) MOD 8;
    FOR c := 0 TO 2 DO                                             (* a sensor still expecting it skips it *)
        IF Sta[c] = Slot THEN
            IF c > 0 AND SensorUsed[c] THEN cMissed := cMissed + 1; END_IF;   (* never seen by sensor c *)
            k := (Slot + 1) MOD 16;
            IF k = QTail THEN Sta[c] := -1; ELSE Sta[c] := k; END_IF;
        END_IF;
    END_FOR;
    QId[Slot] := 0;                                                (* late results are dropped *)
    QHead := (QHead + 1) MOD 16; QCount := QCount - 1;
END_IF;
(* time mode: fire after the delay *)
IF RejCount > 0 THEN
    Age := NowMs - RejT[RejHead];
    IF Age < 0 THEN Age := Age + 3600000; END_IF;
    IF Age >= TIME_TO_DINT(RejectDelay) THEN
        IF Rejecting THEN Retrig := TRUE; END_IF;
        Rejecting := TRUE; cRejCmd := cRejCmd + 1;
        IF RejVer[RejHead] THEN VerAdd := VerAdd + 1; END_IF;
        RejHead := (RejHead + 1) MOD 8; RejCount := RejCount - 1;
    END_IF;
END_IF;

tPulse(IN := Rejecting AND NOT Retrig, PT := RejectOn);
Retrig := FALSE;
IF tPulse.Q THEN Rejecting := FALSE; END_IF;
rVerify(CLK := i_RejectVerify AND NOT ByVerify);
IF ByVerify THEN VerAdd := 0; VerCount := 0; END_IF;
IF rVerify.Q AND VerCount > 0 THEN                                 (* the oldest reject was seen *)
    VerHead := (VerHead + 1) MOD 8; VerCount := VerCount - 1; cRejOk := cRejOk + 1;
END_IF;
IF VerCount > 0 THEN                                               (* the oldest reject not seen in time *)
    Age := NowMs - VerT[VerHead];
    IF Age < 0 THEN Age := Age + 3600000; END_IF;
    IF Age > TIME_TO_DINT(VerifyWin) THEN
        VerHead := (VerHead + 1) MOD 8; VerCount := VerCount - 1; cRejMiss := cRejMiss + 1;
        IF StopOnRejectFail THEN RejectFailAlarm := TRUE; END_IF;
    END_IF;
END_IF;
FOR n := 1 TO VerAdd DO                                            (* this scan's rejects wait for X4 *)
    IF VerCount < 8 THEN
        VerT[(VerHead + VerCount) MOD 8] := NowMs; VerCount := VerCount + 1;
    ELSE                                                           (* more rejects than can be checked *)
        cRejMiss := cRejMiss + 1;
        IF StopOnRejectFail THEN RejectFailAlarm := TRUE; END_IF;
    END_IF;
END_FOR;
VerAdd := 0;

tMinute(IN := NOT tMinute.Q, PT := T#60S);
IF tMinute.Q THEN PartsPerMinute := PartsThisMinute; PartsThisMinute := 0; PpMin := 0; PpMax := 0; END_IF;

(* ---------------- outputs ---------------- *)
AnyWarning := VisWarning OR cRejMiss > 0 OR cTimeouts > 0 OR Learning;
IF NOT AnyFault THEN BuzzerAck := FALSE; END_IF;
q_Rejector    := Rejecting;
q_VisionLight := RunEnable AND (LightOn = T#0S OR (tLightDelay.Q AND NOT tLightOn.Q));
q_VisionReady := VisReady AND NOT VisionLost;
q_LineStop    := NOT ConveyorRun;
q_TowerGreen  := ConveyorRun AND NOT AnyFault AND VisReady;
q_TowerAmber  := (AnyWarning OR Resuming) AND NOT (AnyFault AND NOT Resuming);
q_TowerRed    := AnyFault AND NOT Resuming;
q_Buzzer      := AnyFault AND NOT Resuming AND NOT BuzzerAck;
IF ConveyorRun AND DriveType = 0 THEN VfdCmd := 2; ELSE VfdCmd := 1; END_IF;   (* RS-485 rung writes these to the drive *)

(* conveyor motor by outputs: VFD run terminal, direct-on-line, star-delta (KM1 + KM3 star for the
   star time, both star and delta open for the changeover gap, then KM1 + KM2 delta). KM2 and KM3
   are never on together here; interlock them with NC auxiliary contacts as well. *)
IF NOT ConveyorRun OR DriveType <> 3 THEN SdState := 0;
ELSIF SdState = 0 THEN SdState := 1;
END_IF;
tStar(IN := SdState = 1, PT := StarDeltaT);
IF tStar.Q THEN SdState := 2; END_IF;
tChange(IN := SdState = 2, PT := ChangeT);
IF tChange.Q THEN SdState := 3; END_IF;
q_MotorRun   := (DriveType = 1 OR DriveType = 2) AND ConveyorRun OR (DriveType = 3 AND SdState >= 1);
q_MotorStar  := DriveType = 3 AND SdState = 1;
q_MotorDelta := DriveType = 3 AND SdState = 3 AND NOT q_MotorStar;
VfdFreq := ConveyorHz100;

(* ---------------- input block and camera channels to the vision PC ---------------- *)
VisIn[0] := 0;
IF ResetToVision THEN VisIn[0] := VisIn[0] OR 16#0004; END_IF;
IF RunEnable     THEN VisIn[0] := VisIn[0] OR 16#0008; END_IF;
VisIn[1] := Hb;
VisIn[32] := 0;                                 (* camera channels: bit c = trigger camera c *)
FOR c := 0 TO 2 DO
    IF Cam[c].Trigger THEN VisIn[32] := VisIn[32] OR SHL(WORD#1, c); END_IF;
    VisIn[33 + c] := Cam[c].TriggerId;
END_FOR;

(* ---------------- line diagnostics to the vision PC (section 3.3) ---------------- *)
StateBits := 0;
IF ConveyorRun      THEN StateBits := StateBits OR 16#0001; END_IF;
IF Jam              THEN StateBits := StateBits OR 16#0002; END_IF;
IF BinFull          THEN StateBits := StateBits OR 16#0004; END_IF;
IF NOT AirOk        THEN StateBits := StateBits OR 16#0008; END_IF;
IF NOT EStopOk      THEN StateBits := StateBits OR 16#0010; END_IF;
IF q_LineStop       THEN StateBits := StateBits OR 16#0020; END_IF;
IF RejectFailAlarm  THEN StateBits := StateBits OR 16#0040; END_IF;
IF TrackingAlarm    THEN StateBits := StateBits OR 16#0080; END_IF;
IF EncoderFault     THEN StateBits := StateBits OR 16#0100; END_IF;
IF TriggerStuck     THEN StateBits := StateBits OR 16#0200; END_IF;
IF VisionLost       THEN StateBits := StateBits OR 16#0400; END_IF;
IF NOT VisReady     THEN StateBits := StateBits OR 16#0800; END_IF;
IF DriveFault       THEN StateBits := StateBits OR 16#1000; END_IF;
IF DriveType = 0 AND NOT VfdOnline THEN StateBits := StateBits OR 16#2000; END_IF;
IF Learning         THEN StateBits := StateBits OR 16#4000; END_IF;
IF Resuming         THEN StateBits := StateBits OR 16#8000; END_IF;

Diag[0] := 3;  Diag[1] := 11;                   (* layout 3 (112 bytes); PLC model 11 = DVP12SE11T + DVP16SP11T *)
Diag[2] := 0;                                   (* inputs: X0-X7 bits 0-7, X20-X27 bits 8-15 *)
IF i_Cam1Sensor    THEN Diag[2] := Diag[2] OR 16#0004; END_IF;
IF i_RejectSensor  THEN Diag[2] := Diag[2] OR 16#0008; END_IF;
IF i_RejectVerify  THEN Diag[2] := Diag[2] OR 16#0010; END_IF;
IF i_JamSensor     THEN Diag[2] := Diag[2] OR 16#0020; END_IF;
IF i_AirOk         THEN Diag[2] := Diag[2] OR 16#0040; END_IF;
IF i_EStopOk       THEN Diag[2] := Diag[2] OR 16#0080; END_IF;
IF i_AutoMode      THEN Diag[2] := Diag[2] OR 16#0100; END_IF;
IF i_BinFull       THEN Diag[2] := Diag[2] OR 16#0200; END_IF;
IF i_ResetButton   THEN Diag[2] := Diag[2] OR 16#0400; END_IF;
IF i_VfdFault      THEN Diag[2] := Diag[2] OR 16#0800; END_IF;
IF i_StartButton   THEN Diag[2] := Diag[2] OR 16#4000; END_IF;
IF i_StopButton    THEN Diag[2] := Diag[2] OR 16#8000; END_IF;
IF i_Cam2Sensor    THEN Diag[2] := Diag[2] OR 16#1000; END_IF;
IF i_Cam3Sensor    THEN Diag[2] := Diag[2] OR 16#2000; END_IF;
Diag[3] := 0;                                   (* outputs: Y0-Y3 bits 0-3, Y20-Y27 bits 8-15 *)
IF q_Rejector      THEN Diag[3] := Diag[3] OR 16#0001; END_IF;
IF q_VisionLight   THEN Diag[3] := Diag[3] OR 16#0002; END_IF;
IF q_VisionReady   THEN Diag[3] := Diag[3] OR 16#0004; END_IF;
IF q_LineStop      THEN Diag[3] := Diag[3] OR 16#0008; END_IF;
IF q_TowerGreen    THEN Diag[3] := Diag[3] OR 16#0100; END_IF;
IF q_TowerAmber    THEN Diag[3] := Diag[3] OR 16#0200; END_IF;
IF q_TowerRed      THEN Diag[3] := Diag[3] OR 16#0400; END_IF;
IF q_Buzzer        THEN Diag[3] := Diag[3] OR 16#0800; END_IF;
IF q_MotorRun      THEN Diag[3] := Diag[3] OR 16#1000; END_IF;
IF q_MotorDelta    THEN Diag[3] := Diag[3] OR 16#2000; END_IF;
IF q_MotorStar     THEN Diag[3] := Diag[3] OR 16#4000; END_IF;
Diag[4] := StateBits;
Diag[5] := UINT_TO_WORD(PartsPerMinute);
Diag[6]  := DINT_TO_WORD(EncoderCount);        Diag[7]  := DINT_TO_WORD(SHR(EncoderCount, 16));
Diag[8]  := UDINT_TO_WORD(cTrigSensor);        Diag[9]  := UDINT_TO_WORD(SHR(cTrigSensor, 16));
Diag[10] := UDINT_TO_WORD(cTrigSent);          Diag[11] := UDINT_TO_WORD(SHR(cTrigSent, 16));
Diag[12] := UDINT_TO_WORD(cResults);           Diag[13] := UDINT_TO_WORD(SHR(cResults, 16));
Diag[14] := UDINT_TO_WORD(cMissed);            Diag[15] := UDINT_TO_WORD(SHR(cMissed, 16));
Diag[16] := UDINT_TO_WORD(cTimeouts);          Diag[17] := UDINT_TO_WORD(SHR(cTimeouts, 16));
Diag[18] := UDINT_TO_WORD(cRejCmd);            Diag[19] := UDINT_TO_WORD(SHR(cRejCmd, 16));
Diag[20] := UDINT_TO_WORD(cRejOk);             Diag[21] := UDINT_TO_WORD(SHR(cRejOk, 16));
Diag[22] := UDINT_TO_WORD(cRejMiss);           Diag[23] := UDINT_TO_WORD(SHR(cRejMiss, 16));
Diag[24] := UDINT_TO_WORD(cRejSensor);         Diag[25] := UDINT_TO_WORD(SHR(cRejSensor, 16));
Diag[26] := UDINT_TO_WORD(cTrackMis);          Diag[27] := UDINT_TO_WORD(SHR(cTrackMis, 16));
Diag[28] := UDINT_TO_WORD(cJams);              Diag[29] := UDINT_TO_WORD(SHR(cJams, 16));
Diag[30] := UINT_TO_WORD(PpMin);               Diag[31] := UINT_TO_WORD(PpMax);
Diag[32] := UINT_TO_WORD(StuckMsMax);          Diag[33] := INT_TO_WORD(ConsecNg);
Diag[34] := UDINT_TO_WORD(cEncFaults);         Diag[35] := UDINT_TO_WORD(SHR(cEncFaults, 16));
Diag[36] := UDINT_TO_WORD(cVisLoss);           Diag[37] := UDINT_TO_WORD(SHR(cVisLoss, 16));
Diag[38] := DINT_TO_WORD(LearnedDistance);     Diag[39] := DINT_TO_WORD(SHR(LearnedDistance, 16));
Diag[40] := UDINT_TO_WORD(cCam2);              Diag[41] := UDINT_TO_WORD(SHR(cCam2, 16));
Diag[42] := UDINT_TO_WORD(cCam3);              Diag[43] := UDINT_TO_WORD(SHR(cCam3, 16));
Diag[44] := UDINT_TO_WORD(cNotInspected);      Diag[45] := UDINT_TO_WORD(SHR(cNotInspected, 16));
Diag[46] := Bypass;                            Diag[47] := INT_TO_WORD(Cameras);
Diag[48] := DINT_TO_WORD(LenNom);              Diag[49] := DINT_TO_WORD(SHR(LenNom, 16));
Diag[50] := UDINT_TO_WORD(cTouching);          Diag[51] := UDINT_TO_WORD(SHR(cTouching, 16));
Diag[52] := UDINT_TO_WORD(cLengthOut);         Diag[53] := UDINT_TO_WORD(SHR(cLengthOut, 16));
Diag[54] := UDINT_TO_WORD(cSmallGap);          Diag[55] := UDINT_TO_WORD(SHR(cSmallGap, 16));
END_PROGRAM
