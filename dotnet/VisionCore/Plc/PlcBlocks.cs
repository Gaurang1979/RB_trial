using System.Buffers.Binary;
using System.Text;

namespace VisionCore.Plc;

/// <summary>
/// A raw data block with the byte order of the protocol (docs/vision-system/PLC-INTERFACE.md section 3).
/// Bits are always byte.bit, so byte order never moves them. Big-endian: PROFINET and S7. Little-endian
/// words, low word first: Modbus (byte 0 = low byte of register 0), MC, FINS, EtherNet/IP.
/// </summary>
public sealed class PlcBlock
{
    public PlcBlock(int size, bool bigEndian)
    {
        Bytes = new byte[size];
        BigEndian = bigEndian;
    }

    public byte[] Bytes { get; }
    public bool BigEndian { get; }

    public bool Bit(int b, int bit) => (Bytes[b] & (1 << bit)) != 0;
    public void SetBit(int b, int bit, bool v)
    {
        if (v) Bytes[b] |= (byte)(1 << bit);
        else Bytes[b] &= (byte)~(1 << bit);
    }

    public ushort U16(int at) => BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan(at)) : BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(at));
    public void SetU16(int at, ushort v)
    {
        if (BigEndian) BinaryPrimitives.WriteUInt16BigEndian(Bytes.AsSpan(at), v);
        else BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(at), v);
    }
    public uint U32(int at) => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan(at)) : BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(at));
    public void SetU32(int at, uint v)
    {
        if (BigEndian) BinaryPrimitives.WriteUInt32BigEndian(Bytes.AsSpan(at), v);
        else BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(at), v);
    }
    public int I32(int at) => unchecked((int)U32(at));
    public void SetReal(int at, float v) => SetU32(at, BitConverter.SingleToUInt32Bits(v));

    /// <summary>S7 string layout: byte max length, byte actual length, then the characters.</summary>
    public string Str(int at)
    {
        int max = Bytes[at], len = Math.Min(Bytes[at + 1], max);
        return Encoding.ASCII.GetString(Bytes, at + 2, len);
    }
    /// <summary>Writes an S7 string; returns false when the text was cut to fit.</summary>
    public bool SetStr(int at, int max, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        bool cut = b.Length > max;
        int n = Math.Min(b.Length, max);
        Bytes[at] = (byte)max;
        Bytes[at + 1] = (byte)n;
        Array.Clear(Bytes, at + 2, max);
        Array.Copy(b, 0, Bytes, at + 2, n);
        return !cut;
    }
}

/// <summary>PLC → vision, 64 bytes (section 3.1).</summary>
public sealed class PlcInput
{
    public const int Size = 64;
    public PlcInput(PlcBlock b) => B = b;
    public PlcBlock B { get; }

    public bool Trigger => B.Bit(0, 0);
    public bool JobChange => B.Bit(0, 1);
    public bool ResetAlarm => B.Bit(0, 2);
    public bool RunEnable => B.Bit(0, 3);
    public bool ResultAck => B.Bit(0, 4);
    public bool SaveImage => B.Bit(0, 5);
    public ushort Heartbeat => B.U16(2);
    public ushort JobSelect => B.U16(4);
    public ushort LightStep => B.U16(6);
    public ushort TriggerId => B.U16(8);
    public ushort CameraMask => B.U16(10);
    public uint ProductId => B.U32(12);
    public string PartId => B.Str(16);
    public int UserIn(int i) => B.I32(48 + 4 * i);
    public int[] UserIns => Enumerable.Range(0, 4).Select(UserIn).ToArray();
}

/// <summary>How a string is laid out in the text block (PLC-INTERFACE.md section 3.7).</summary>
public enum PlcTextEncoding
{
    /// <summary>One ASCII character per byte; a NUL ends it, trailing spaces are padding.</summary>
    Ascii,
    /// <summary>S7 string: byte max length, byte actual length, then the ASCII characters (as PartID).</summary>
    S7String,
}

/// <summary>Which byte of a register holds the first of its two characters.</summary>
public enum PlcByteOrder
{
    /// <summary>High byte first: Siemens, Delta and most Modbus string moves.</summary>
    HighFirst,
    /// <summary>Low byte first: some Mitsubishi, Omron and Modbus masters.</summary>
    LowFirst,
}

/// <summary>Where one string lies in the text block; kept in the job with the Check text line.</summary>
public sealed class PlcTextSpec
{
    /// <summary>First holding register of the string, <see cref="PlcTextBlock.FirstDataRegister"/> to <see cref="PlcTextBlock.LastRegister"/>.</summary>
    public int Register { get; set; } = PlcTextBlock.FirstDataRegister;
    /// <summary>Most characters the string holds, 1 to <see cref="PlcTextBlock.DataBytes"/>.</summary>
    public int Length { get; set; } = 40;
    public PlcTextEncoding Encoding { get; set; } = PlcTextEncoding.Ascii;
    public PlcByteOrder ByteOrder { get; set; } = PlcByteOrder.HighFirst;

    /// <summary>Bytes the string takes, header included.</summary>
    public int Bytes => Length + (Encoding == PlcTextEncoding.S7String ? 2 : 0);

    /// <summary>Why this place cannot be read, or null when it fits the block.</summary>
    public string? Problem()
    {
        if (Length < 1 || Length > PlcTextBlock.DataBytes - (Encoding == PlcTextEncoding.S7String ? 2 : 0))
            return $"the string length must be 1 to {PlcTextBlock.DataBytes - (Encoding == PlcTextEncoding.S7String ? 2 : 0)} characters";
        int last = Register + (Bytes + 1) / 2 - 1;
        if (Register < PlcTextBlock.FirstDataRegister || last > PlcTextBlock.LastRegister)
            return $"the string must lie in registers {PlcTextBlock.FirstDataRegister} to {PlcTextBlock.LastRegister} (it takes {Register} to {last})";
        return null;
    }
}

/// <summary>
/// Text block (PLC-INTERFACE.md section 3.7): PLC → vision, 256 bytes at Modbus holding registers 500-627,
/// for expected texts longer than PartID. Register 500 is <c>TextTriggerId</c>: the PLC writes the strings
/// first, then the TriggerID of the trigger they belong to, then sets the trigger. Registers 501-627 hold
/// the strings wherever the job places them.
/// </summary>
public static class PlcTextBlock
{
    public const int Size = 256, StartRegister = 500, FirstDataRegister = 501, LastRegister = StartRegister + Size / 2 - 1;
    public const int DataBytes = Size - 2;

    public static ushort TriggerId(PlcBlock b) => b.U16(0);
}

/// <summary>
/// The text block as it was when a trigger was taken. <see cref="Fresh"/> is false when the block's
/// TextTriggerId is not this trigger's TriggerID, the TriggerID is 0, or the same TriggerID was taken before
/// (the strings may belong to another part); every string from it is then missing (line safety: NG).
/// </summary>
public sealed class PlcText
{
    private readonly PlcBlock _b;

    public PlcText(byte[] block, bool bigEndian, ushort triggerId, bool repeated)
    {
        _b = new PlcBlock(PlcTextBlock.Size, bigEndian);
        block.AsSpan(0, Math.Min(block.Length, PlcTextBlock.Size)).CopyTo(_b.Bytes);
        TriggerId = triggerId;
        BlockTriggerId = block.Length >= 2 ? PlcTextBlock.TriggerId(_b) : (ushort)0;
        Stale = block.Length < PlcTextBlock.Size ? "the PLC has no text block"
              : triggerId == 0 ? "the trigger has TriggerID 0; the text block needs a new TriggerID for every part"
              : repeated ? $"TriggerID {triggerId} was used before; the text block needs a new TriggerID for every part"
              : BlockTriggerId != triggerId ? $"the text block is for TriggerID {BlockTriggerId}, not this trigger's {triggerId}"
              : null;
    }

    public ushort TriggerId { get; }
    public ushort BlockTriggerId { get; }
    /// <summary>Why the block does not belong to this trigger; null when it does.</summary>
    public string? Stale { get; }
    public bool Fresh => Stale is null;

    /// <summary>The string at <paramref name="spec"/>; null, with the reason, when it is stale, empty or unreadable.</summary>
    public string? Get(PlcTextSpec spec, out string why)
    {
        why = "";
        if (Stale is { } s) { why = s; return null; }
        if (spec.Problem() is { } p) { why = p; return null; }
        // the characters in PLC order, two per register
        int at = 2 * (spec.Register - PlcTextBlock.StartRegister);
        var raw = new byte[spec.Bytes + 1 & ~1];
        for (int i = 0; i < raw.Length; i += 2)
        {
            ushort w = _b.U16(at + i);
            (raw[i], raw[i + 1]) = spec.ByteOrder == PlcByteOrder.HighFirst ? ((byte)(w >> 8), (byte)w) : ((byte)w, (byte)(w >> 8));
        }
        int start = 0, len;
        if (spec.Encoding == PlcTextEncoding.S7String)
        {
            int max = raw[0];
            len = raw[1];
            if (len > max || len > spec.Length) { why = $"the S7 string says {len} characters in {Math.Min(max, spec.Length)}"; return null; }
            start = 2;
        }
        else
        {
            len = Array.IndexOf(raw, (byte)0, 0, spec.Length) is var nul and >= 0 ? nul : spec.Length;
        }
        for (int i = start; i < start + len; i++)
            if (raw[i] is < 0x20 or > 0x7E) { why = $"character {i - start + 1} is not printable ASCII (byte {raw[i]})"; return null; }
        var text = System.Text.Encoding.ASCII.GetString(raw, start, len).Trim();
        if (text.Length == 0) { why = "the string is empty"; return null; }
        return text;
    }
}

/// <summary>Vision → PLC, 256 bytes (section 3.2).</summary>
public sealed class PlcOutput
{
    public const int Size = 256;
    public const ushort Layout = 1;
    public PlcOutput(PlcBlock b) { B = b; B.SetU16(32, Layout); }
    public PlcBlock B { get; }

    public bool Ready { get => B.Bit(0, 0); set => B.SetBit(0, 0, value); }
    public bool Busy { get => B.Bit(0, 1); set => B.SetBit(0, 1, value); }
    public bool Done { get => B.Bit(0, 2); set => B.SetBit(0, 2, value); }
    public bool Ok { get => B.Bit(0, 3); set => B.SetBit(0, 3, value); }
    public bool Ng { get => B.Bit(0, 4); set => B.SetBit(0, 4, value); }
    public bool Error { get => B.Bit(0, 5); set => B.SetBit(0, 5, value); }
    public bool JobActive { get => B.Bit(0, 6); set => B.SetBit(0, 6, value); }
    public bool JobChangeDone { get => B.Bit(0, 7); set => B.SetBit(0, 7, value); }
    public bool ExposureDone { get => B.Bit(1, 0); set => B.SetBit(1, 0, value); }
    public bool TriggerRejected { get => B.Bit(1, 2); set => B.SetBit(1, 2, value); }
    public bool Warning { get => B.Bit(1, 3); set => B.SetBit(1, 3, value); }
    public bool Simulation { get => B.Bit(1, 4); set => B.SetBit(1, 4, value); }
    public ushort HeartbeatEcho { get => B.U16(2); set => B.SetU16(2, value); }
    public ushort VisionHeartbeat { get => B.U16(4); set => B.SetU16(4, value); }
    public ushort ErrorCode { get => B.U16(6); set => B.SetU16(6, value); }
    public ushort ActiveJob { get => B.U16(8); set => B.SetU16(8, value); }
    public ushort TriggerIdEcho { get => B.U16(10); set => B.SetU16(10, value); }
    public ushort NgReason { get => B.U16(14); set => B.SetU16(14, value); }
    public uint InspectionCount { get => B.U32(16); set => B.SetU32(16, value); }
    public float CycleTime { set => B.SetReal(20, value); }
    public uint ToolPass { get => B.U32(28); set => B.SetU32(28, value); }
    public ushort WarningCode { get => B.U16(34); set => B.SetU16(34, value); }
    public void SetValue(int i, float v) => B.SetReal(40 + 4 * i, v);
    public bool SetText1(string s) => B.SetStr(120, 66, s);
    public bool SetText2(string s) => B.SetStr(188, 66, s);
}

/// <summary>Error and warning codes (section 9) used by this runtime.</summary>
public static class PlcCodes
{
    public const ushort HeartbeatLost = 100;
    public const ushort TriggerWhileBusy = 110;
    public const ushort TriggerNotReady = 111;
    public const ushort JobNotFound = 200;
    public const ushort JobChangeWhileBusy = 203;
    public const ushort CameraNotFound = 300;
    public const ushort NoFrame = 302;
    public const ushort ToolFailed = 400;
    public const ushort Restarted = 502;
    public const ushort StringCut = 610;
}

/// <summary>
/// Camera channels (PLC-INTERFACE.md section 3.5): one trigger and one result per camera, for lines
/// where the cameras stand at different places and each is fired when the part reaches it.
/// PLC → vision 16 bytes (Modbus registers 32-39), vision → PLC 32 bytes (registers 228-243).
/// </summary>
public static class PlcCameraChannels
{
    public const int InSize = 16, OutSize = 32, Max = 3;

    public static bool Trigger(PlcBlock inB, int cam) => inB.Bit(0, cam);
    public static ushort TriggerId(PlcBlock inB, int cam) => inB.U16(2 + 2 * cam);

    // per camera, 8 bytes at 8 x cam: status bits, trigger id echo, first failing tool, cycle time ms
    public static void SetStatus(PlcBlock o, int cam, bool ready, bool busy, bool done, bool ok, bool rejected)
    {
        int b = 8 * cam;
        o.SetBit(b, 0, ready); o.SetBit(b, 1, busy); o.SetBit(b, 2, done);
        o.SetBit(b, 3, done && ok); o.SetBit(b, 4, done && !ok); o.SetBit(b, 5, rejected);
    }
    public static bool Busy(PlcBlock o, int cam) => o.Bit(8 * cam, 1);
    public static bool Done(PlcBlock o, int cam) => o.Bit(8 * cam, 2);
    public static bool Ok(PlcBlock o, int cam) => o.Bit(8 * cam, 3);
    public static bool Ready(PlcBlock o, int cam) => o.Bit(8 * cam, 0);
    public static bool Rejected(PlcBlock o, int cam) => o.Bit(8 * cam, 5);
    public static ushort IdEcho(PlcBlock o, int cam) => o.U16(8 * cam + 2);
    public static void SetResult(PlcBlock o, int cam, ushort idEcho, ushort ngReason, ushort cycleMs)
    {
        o.SetU16(8 * cam + 2, idEcho);
        o.SetU16(8 * cam + 4, ngReason);
        o.SetU16(8 * cam + 6, cycleMs);
    }
}

/// <summary>Why a part was rejected (or flagged), from the PLC's part record (PLC-INTERFACE.md section 3.6).</summary>
[Flags]
public enum PartFlags : ushort
{
    None = 0,
    Rejected = 1 << 0,          // the rejector fired for it
    CameraNg = 1 << 1,          // a camera said NOT OK
    NotInspected = 1 << 2,      // a camera did not answer, was busy or not ready
    MissedAtSensor = 1 << 3,    // passed a camera's trigger sensor unseen
    Touching = 1 << 4,          // came touching other products (no gap): one sensor signal for several
    LengthOut = 1 << 5,         // length out of tolerance (fallen, lying, broken, half pack)
    SmallGap = 1 << 6,          // gap to the product before below the minimum (reported, not rejected)
    Learning = 1 << 7,          // first part after a start: distances learned on it
}

/// <summary>One product leaving the line: what the PLC decided and measured.</summary>
public sealed record PartRecord(ushort Seq, ushort PartId, PartFlags Flags, int LengthPulses, int GapPulses,
                                byte CamAnswered, byte CamOk, int GroupSize, int GroupIndex)
{
    public bool Rejected => Flags.HasFlag(PartFlags.Rejected);
    public bool CameraOk(int c) => (CamOk >> c & 1) != 0;
    public bool CameraAnswered(int c) => (CamAnswered >> c & 1) != 0;
}

/// <summary>
/// Part log (section 3.6): PLC → vision, 96 bytes at Modbus holding registers 400-447. A ring of 8
/// records of 6 registers; the PLC writes each product leaving the queue into the next record with a
/// new sequence number (never 0), so the vision PC sees every part even at 10 parts a second.
/// Record: seq, part id, flags, length (pulses, max 65535), gap before it (pulses, max 65535),
/// cameras (bits 0-2 answered, 4-6 OK, 8-11 group size, 12-15 index in the group).
/// </summary>
public static class PlcPartLog
{
    public const int Size = 96, Records = 8, RecordBytes = 12;

    public static PartRecord? Read(PlcBlock b, int i)
    {
        int at = i * RecordBytes;
        ushort seq = b.U16(at);
        if (seq == 0) return null;
        ushort cams = b.U16(at + 10);
        return new PartRecord(seq, b.U16(at + 2), (PartFlags)b.U16(at + 4), b.U16(at + 6), b.U16(at + 8),
            (byte)(cams & 7), (byte)(cams >> 4 & 7), Math.Max(1, cams >> 8 & 15), cams >> 12 & 15);
    }

    public static void Write(PlcBlock b, int i, PartRecord r)
    {
        int at = i * RecordBytes;
        b.SetU16(at, r.Seq); b.SetU16(at + 2, r.PartId); b.SetU16(at + 4, (ushort)r.Flags);
        b.SetU16(at + 6, (ushort)Math.Clamp(r.LengthPulses, 0, 65535)); b.SetU16(at + 8, (ushort)Math.Clamp(r.GapPulses, 0, 65535));
        b.SetU16(at + 10, (ushort)(r.CamAnswered & 7 | (r.CamOk & 7) << 4 | (r.GroupSize & 15) << 8 | (r.GroupIndex & 15) << 12));
    }

    /// <summary>Records newer than <paramref name="lastSeq"/>, oldest first (sequence numbers wrap and skip 0).</summary>
    public static List<PartRecord> Newer(PlcBlock b, ushort lastSeq)
    {
        var all = Enumerable.Range(0, Records).Select(i => Read(b, i)).OfType<PartRecord>().ToList();
        static int Ahead(ushort from, ushort to) => (ushort)(to - from) is var d && d < 0x8000 ? d : -1;
        return all.Where(r => Ahead(lastSeq, r.Seq) > 0).OrderBy(r => Ahead(lastSeq, r.Seq)).ToList();
    }
}
