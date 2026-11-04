(* ======================================================================================
   RBTrialDVP - Delta DVP slim (DVP12SE11T + DVP16SP11T), ISPSoft, Structured Text
   FACTORY TRIAL of the RB line before the client site. It does towards the vision PC what the client's
   Allen-Bradley Micro850 program HARPIK08042026_Final_141026 does, with the same register map, so
   the vision PC runs the same Micro850 link (vs-runtime.json "Micro850", RegisterBase 4096) in the
   factory and at the client:
     product sensor X2 (falling edge, as the Micro850) -> the product gets an encoder position (slot) ->
     at encoder distances: camera trigger outputs Y0 (cameras 1+2), Y1 (camera 3), Y2 (camera 4) and
     the light Y3 -> result window from D47 to D44: the vision PC writes D3 = 1 (PASS) or D4 = 1 (FAIL)
     -> at D1 the rejector Y20 fires for every product without a PASS -> reject verification X3.
     Vision heartbeat D43 bit 1 unchanged 5 s while running = "software crash": the line stops.
   Safer than the Micro850 program in one point: a PASS that arrives while two products are in the
   result window is given to neither (both rejected, alarm "window longer than the product pitch").

   Registers = Modbus TCP holding registers of the DVP-SE server: D0 = 4096 (16#1000), D49 = 4145.
   Check the D <-> Modbus address table in the DVP-SE manual for your firmware.

   Map (same numbers as mod_address[] of the Micro850 program):
     D0  trigger 1 distance (pulses)      D46 trigger 2 distance      D47 trigger 3 distance = window opens
     D44 window closes                    D1  reject distance         D2  reject on time (ms)
     D28 reject verification time (ms)    D39 rejects in a row -> stop
     D40 infeed jam (s)  D41 outfeed jam (s)  D42 bin full (s)
     D48 light distance                   D49 light on time (ms)      D29 trigger pulse (ms, 0 = 40)
     D3  PASS from the PC (cleared after 50 ms)   D4 FAIL from the PC (cleared after 100 ms)
     D5  soft conveyor start (PC)         D7  alarm acknowledge (PC)
     D43 bit 0 soft reset (PC), bit 1 vision heartbeat (PC), bit 2 conveyor running, bit 3 bin full
     D45 bypass bits: 6 infeed jam, 7 reject verification, 8 outfeed jam, 9 bin full
     D25 encoder (low word)   D26 PASS count   D27 reject count
     D35 inputs X0-X7, X20-X27   D36 outputs Y0-Y3, Y20-Y27
     D37 alarms: 8 infeed jam, 9 E-stop, 10 air, 11 reject verification failed, 12 outfeed jam,
                 13 bin full, 14 rejects in a row, 15 vision heartbeat lost, 0 window too long
   Defaults are written on the first scan when D44 is 0 (D0-D49 are not latched on the DVP-SE).

   Not compiled in ISPSoft here; plc/st_check.sh checks syntax and types with rusty. On the first
   ISPSoft build check: (1) TON / R_TRIG / F_TRIG in your ISPSoft library, (2) the ladder rung that
   starts C251 (DCNT C251 K2147483647, A/B phase X0/X1) and copies it every scan (DMOV C251 D200),
   (3) the module's I/O numbers, (4) scan time: below 5 ms keeps the trigger within a few mm at
   225 products/min; for tighter triggers use the high-speed compare (DHSCS) on C251.
   ====================================================================================== *)

PROGRAM RBTrialDVP
VAR_GLOBAL
    (* inputs: CPU X0-X7, DVP16SP11T X20-X27 *)
    i_EncA          AT %IX0  : BOOL;   (* encoder A: high-speed counter C251 *)
    i_EncB          AT %IX1  : BOOL;   (* encoder B *)
    i_Product       AT %IX2  : BOOL;   (* product sensor (camera trigger sensor) *)
    i_RejectVerify  AT %IX3  : BOOL;   (* across the reject chute *)
    i_StartButton   AT %IX4  : BOOL;   (* green, NO *)
    i_StopButton    AT %IX5  : BOOL;   (* red, NC: TRUE = not pressed *)
    i_ResetButton   AT %IX6  : BOOL;
    i_EStopOk       AT %IX7  : BOOL;   (* safety relay auxiliary, closed = healthy *)
    i_InfeedJam     AT %IX20 : BOOL;
    i_OutfeedJam    AT %IX21 : BOOL;
    i_BinFull       AT %IX22 : BOOL;
    i_AirOk         AT %IX23 : BOOL;
    (* outputs: CPU Y0-Y3 (fast transistor), DVP16SP11T Y20-Y27 *)
    q_Trig1         AT %QX0  : BOOL;   (* cameras 1 and 2, Line0 *)
    q_Trig2         AT %QX1  : BOOL;   (* camera 3 *)
    q_Trig3         AT %QX2  : BOOL;   (* camera 4 *)
    q_Light         AT %QX3  : BOOL;
    q_Rejector      AT %QX20 : BOOL;
    q_Conveyor      AT %QX21 : BOOL;   (* motor run (contactor relay or VFD FWD) *)
    q_TowerGreen    AT %QX22 : BOOL;
    q_TowerAmber    AT %QX23 : BOOL;
    q_TowerRed      AT %QX24 : BOOL;
    q_Buzzer        AT %QX25 : BOOL;
    Reg     AT %MW0   : ARRAY[0..49] OF INT;   (* D0-D49: the vision PC's registers *)
    EncoderCount AT %MD200 : DINT;              (* D200/D201: C251 copied by the ladder rung every scan *)
END_VAR

VAR CONSTANT
    Slots : INT := 8;                  (* products between the sensor and the rejector *)
END_VAR

VAR
    FirstScan : BOOL := TRUE;
    (* product tracking: one slot per product from the sensor to the rejector *)
    SActive, SNg, SOk, SResult, STrig1, STrig2, STrig3, SLight, SDone : ARRAY[0..7] OF BOOL;
    SPos : ARRAY[0..7] OF DINT;
    s, k, InWindow, WinSlot : INT;
    d : DINT;
    fProduct : F_TRIG;
    rPass, rFail, rReset, rStart, rVerify, rSoftStart, rAck : R_TRIG;
    tPassClr, tFailClr : TON;
    (* outputs: pulse timers *)
    tTrig1, tTrig2, tTrig3, tLight, tReject : TON;
    Trig1, Trig2, Trig3, Light, Reject : BOOL;
    TrigPulse, LightOn, RejectOn, VerifyT : TIME;
    (* reject verification: one verify edge per reject within VerifyT, oldest first *)
    VerPending : INT;
    tVerify : TON;
    (* alarms and line *)
    tHb : TON; HbLast : BOOL;
    tInJam, tOutJam, tBin : TON;
    Running, AlarmWindow, AlarmVerify, AlarmConsec, AlarmHb, AlarmInJam, AlarmOutJam, AlarmBin : BOOL;
    AnyAlarm, BuzzerOff, Bypass6, Bypass7, Bypass8, Bypass9, Pass : BOOL;
    Consec : INT;
    PassCount, RejectCount : INT;
    w : WORD;
END_VAR

(* ---- first scan: defaults (same as the Micro850 program's initial values) *)
IF FirstScan THEN
    FirstScan := FALSE;
    IF Reg[44] = 0 THEN
        Reg[0] := 30; Reg[1] := 500; Reg[2] := 40; Reg[28] := 200; Reg[29] := 40; Reg[39] := 10;
        Reg[40] := 5; Reg[41] := 5; Reg[42] := 5; Reg[44] := 400; Reg[46] := 35; Reg[47] := 170;
        Reg[48] := 1; Reg[49] := 200;
    END_IF;
    FOR s := 0 TO Slots - 1 DO SActive[s] := FALSE; END_FOR;
END_IF;

TrigPulse := INT_TO_TIME(Reg[29]);
IF Reg[29] <= 0 THEN TrigPulse := T#40MS; END_IF;
LightOn := INT_TO_TIME(Reg[49]);
RejectOn := INT_TO_TIME(Reg[2]);
VerifyT := INT_TO_TIME(Reg[28]);
w := INT_TO_WORD(Reg[45]);
Bypass6 := (w AND 16#0040) <> 0; Bypass7 := (w AND 16#0080) <> 0;
Bypass8 := (w AND 16#0100) <> 0; Bypass9 := (w AND 16#0200) <> 0;

(* ---- results from the vision PC: an edge of D3 / D4; the PLC clears them as the Micro850 does *)
rPass(CLK := Reg[3] >= 1);
rFail(CLK := Reg[4] >= 1);
tPassClr(IN := Reg[3] >= 1, PT := T#50MS);
IF tPassClr.Q THEN Reg[3] := 0; END_IF;
tFailClr(IN := Reg[4] >= 1, PT := T#100MS);
IF tFailClr.Q THEN Reg[4] := 0; END_IF;

IF rPass.Q OR rFail.Q THEN
    (* which products are in the result window now *)
    InWindow := 0; WinSlot := -1;
    FOR s := 0 TO Slots - 1 DO
        IF SActive[s] AND NOT SResult[s] THEN
            d := EncoderCount - SPos[s];
            IF d >= INT_TO_DINT(Reg[47]) AND d <= INT_TO_DINT(Reg[44]) THEN
                InWindow := InWindow + 1; WinSlot := s;
            END_IF;
        END_IF;
    END_FOR;
    IF InWindow = 1 THEN
        SResult[WinSlot] := TRUE;
        SOk[WinSlot] := rPass.Q AND NOT rFail.Q;
        SNg[WinSlot] := NOT SOk[WinSlot];
    ELSIF InWindow > 1 THEN
        (* the result cannot be given to one product: all of them are rejected *)
        AlarmWindow := TRUE;
        FOR s := 0 TO Slots - 1 DO
            IF SActive[s] AND NOT SResult[s] THEN
                d := EncoderCount - SPos[s];
                IF d >= INT_TO_DINT(Reg[47]) AND d <= INT_TO_DINT(Reg[44]) THEN
                    SResult[s] := TRUE; SNg[s] := TRUE; SOk[s] := FALSE;
                END_IF;
            END_IF;
        END_FOR;
    END_IF;
    (* a result outside every window belongs to no product and is dropped *)
END_IF;

(* ---- a new product at the sensor *)
fProduct(CLK := i_Product);
IF fProduct.Q AND Running THEN
    k := -1;
    FOR s := 0 TO Slots - 1 DO
        IF k < 0 AND NOT SActive[s] THEN k := s; END_IF;
    END_FOR;
    IF k >= 0 THEN
        SActive[k] := TRUE; SPos[k] := EncoderCount;
        SNg[k] := FALSE; SOk[k] := FALSE; SResult[k] := FALSE;
        STrig1[k] := FALSE; STrig2[k] := FALSE; STrig3[k] := FALSE; SLight[k] := FALSE; SDone[k] := FALSE;
    ELSE
        AlarmWindow := TRUE;            (* more products than slots: settings wrong, product untracked *)
    END_IF;
END_IF;

(* ---- every product along the line *)
FOR s := 0 TO Slots - 1 DO
    IF SActive[s] THEN
        d := EncoderCount - SPos[s];
        IF NOT STrig1[s] AND d >= INT_TO_DINT(Reg[0]) THEN STrig1[s] := TRUE; Trig1 := TRUE; END_IF;
        IF NOT STrig2[s] AND d >= INT_TO_DINT(Reg[46]) THEN STrig2[s] := TRUE; Trig2 := TRUE; END_IF;
        IF NOT STrig3[s] AND d >= INT_TO_DINT(Reg[47]) THEN STrig3[s] := TRUE; Trig3 := TRUE; END_IF;
        IF NOT SLight[s] AND d >= INT_TO_DINT(Reg[48]) THEN SLight[s] := TRUE; Light := TRUE; END_IF;
        (* no result by the end of the window: rejected *)
        IF NOT SResult[s] AND d > INT_TO_DINT(Reg[44]) THEN SResult[s] := TRUE; SNg[s] := TRUE; END_IF;
        (* at the rejector *)
        IF NOT SDone[s] AND d >= INT_TO_DINT(Reg[1]) THEN
            SDone[s] := TRUE;
            Pass := SResult[s] AND SOk[s] AND NOT SNg[s];
            IF Pass THEN
                PassCount := PassCount + 1; Consec := 0;
            ELSE
                Reject := TRUE; RejectCount := RejectCount + 1; Consec := Consec + 1;
                IF NOT Bypass7 THEN VerPending := VerPending + 1; END_IF;
            END_IF;
            SActive[s] := FALSE;
        END_IF;
    END_IF;
END_FOR;

(* ---- output pulses (a new pulse while one runs is joined to it) *)
tTrig1(IN := Trig1, PT := TrigPulse); IF tTrig1.Q THEN Trig1 := FALSE; END_IF;
tTrig2(IN := Trig2, PT := TrigPulse); IF tTrig2.Q THEN Trig2 := FALSE; END_IF;
tTrig3(IN := Trig3, PT := TrigPulse); IF tTrig3.Q THEN Trig3 := FALSE; END_IF;
tLight(IN := Light, PT := LightOn);   IF tLight.Q THEN Light := FALSE; END_IF;
tReject(IN := Reject, PT := RejectOn); IF tReject.Q THEN Reject := FALSE; END_IF;

(* ---- reject verification: each reject needs its verify edge within VerifyT *)
rVerify(CLK := i_RejectVerify);
IF rVerify.Q AND VerPending > 0 THEN VerPending := VerPending - 1; END_IF;
tVerify(IN := VerPending > 0 AND NOT Reject, PT := VerifyT);
IF tVerify.Q THEN AlarmVerify := TRUE; VerPending := 0; END_IF;

(* ---- alarms *)
IF Consec >= Reg[39] AND Reg[39] > 0 THEN AlarmConsec := TRUE; END_IF;
w := INT_TO_WORD(Reg[43]);
tHb(IN := Running AND (((w AND 16#0002) <> 0) = HbLast), PT := T#5S);
HbLast := (w AND 16#0002) <> 0;
IF tHb.Q THEN AlarmHb := TRUE; END_IF;
tInJam(IN := Running AND i_InfeedJam AND NOT Bypass6, PT := INT_TO_TIME(Reg[40] * 1000));
IF tInJam.Q THEN AlarmInJam := TRUE; END_IF;
tOutJam(IN := Running AND i_OutfeedJam AND NOT Bypass8, PT := INT_TO_TIME(Reg[41] * 1000));
IF tOutJam.Q THEN AlarmOutJam := TRUE; END_IF;
tBin(IN := i_BinFull AND NOT Bypass9, PT := INT_TO_TIME(Reg[42] * 1000));
IF tBin.Q THEN AlarmBin := TRUE; END_IF;
AnyAlarm := AlarmWindow OR AlarmVerify OR AlarmConsec OR AlarmHb OR AlarmInJam OR AlarmOutJam OR AlarmBin
            OR NOT i_EStopOk OR NOT i_AirOk;

(* ---- reset: button, D43 bit 0 or D7 from the PC *)
rReset(CLK := i_ResetButton OR (w AND 16#0001) <> 0 OR Reg[7] >= 1);
IF rReset.Q THEN
    AlarmWindow := FALSE; AlarmVerify := FALSE; AlarmConsec := FALSE; AlarmHb := FALSE;
    AlarmInJam := FALSE; AlarmOutJam := FALSE; AlarmBin := FALSE; Consec := 0; VerPending := 0;
    BuzzerOff := FALSE;
    Reg[43] := WORD_TO_INT(w AND 16#FFFE); Reg[7] := 0;
END_IF;

(* ---- conveyor: start button or D5 from the PC; stop button, E-stop or any alarm stops it *)
rStart(CLK := i_StartButton);
rSoftStart(CLK := Reg[5] >= 1);
IF Reg[5] >= 1 THEN Reg[5] := 0; END_IF;
IF (rStart.Q OR rSoftStart.Q) AND NOT AnyAlarm AND i_StopButton THEN Running := TRUE; HbLast := (w AND 16#0002) <> 0; END_IF;
IF NOT i_StopButton OR AnyAlarm THEN Running := FALSE; END_IF;
IF NOT Running THEN
    (* products on a stopped line are forgotten: they pass the sensor again after a restart *)
    FOR s := 0 TO Slots - 1 DO SActive[s] := FALSE; END_FOR;
END_IF;

(* ---- outputs *)
q_Trig1 := Trig1;
q_Trig2 := Trig2;
q_Trig3 := Trig3;
q_Light := Light;
q_Rejector := Reject;
q_Conveyor := Running;
q_TowerGreen := Running;
q_TowerAmber := NOT Running AND NOT AnyAlarm;
q_TowerRed := AnyAlarm;
IF Reg[7] >= 1 THEN BuzzerOff := TRUE; END_IF;
q_Buzzer := AnyAlarm AND NOT BuzzerOff;

(* ---- status for the vision PC (log): D25-D27, D35-D37, D43 bits 2-3 *)
Reg[25] := DINT_TO_INT(EncoderCount MOD 32768);
Reg[26] := PassCount;
Reg[27] := RejectCount;
w := 0;
IF i_EncA THEN w := w OR 16#0001; END_IF;   IF i_EncB THEN w := w OR 16#0002; END_IF;
IF i_Product THEN w := w OR 16#0004; END_IF; IF i_RejectVerify THEN w := w OR 16#0008; END_IF;
IF i_StartButton THEN w := w OR 16#0010; END_IF; IF i_StopButton THEN w := w OR 16#0020; END_IF;
IF i_ResetButton THEN w := w OR 16#0040; END_IF; IF i_EStopOk THEN w := w OR 16#0080; END_IF;
IF i_InfeedJam THEN w := w OR 16#0100; END_IF; IF i_OutfeedJam THEN w := w OR 16#0200; END_IF;
IF i_BinFull THEN w := w OR 16#0400; END_IF; IF i_AirOk THEN w := w OR 16#0800; END_IF;
Reg[35] := WORD_TO_INT(w);
w := 0;
IF q_Trig1 THEN w := w OR 16#0001; END_IF; IF q_Trig2 THEN w := w OR 16#0002; END_IF;
IF q_Trig3 THEN w := w OR 16#0004; END_IF; IF q_Light THEN w := w OR 16#0008; END_IF;
IF q_Rejector THEN w := w OR 16#0010; END_IF; IF q_Conveyor THEN w := w OR 16#0020; END_IF;
Reg[36] := WORD_TO_INT(w);
w := 0;
IF AlarmWindow THEN w := w OR 16#0001; END_IF;
IF AlarmInJam THEN w := w OR 16#0100; END_IF;  IF NOT i_EStopOk THEN w := w OR 16#0200; END_IF;
IF NOT i_AirOk THEN w := w OR 16#0400; END_IF; IF AlarmVerify THEN w := w OR 16#0800; END_IF;
IF AlarmOutJam THEN w := w OR 16#1000; END_IF; IF AlarmBin THEN w := w OR 16#2000; END_IF;
IF AlarmConsec THEN w := w OR 16#4000; END_IF; IF AlarmHb THEN w := w OR 16#8000; END_IF;
Reg[37] := WORD_TO_INT(w);
w := INT_TO_WORD(Reg[43]) AND 16#0003;
IF Running THEN w := w OR 16#0004; END_IF;
IF i_BinFull THEN w := w OR 16#0008; END_IF;
Reg[43] := WORD_TO_INT(w);
END_PROGRAM
