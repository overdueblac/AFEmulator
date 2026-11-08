using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches;

public sealed partial class AfdevNativeBootstrap
{
    private const uint ListenPortRead = 0x0127F250;
    private const string ListenPortReadOriginal = "8B4C24508B4118";
    private uint listenPortTrace;
    private bool reportedListenPort;

    private static readonly (uint Address, string Original)[] ListenPortAnchors =
    [
        (0x0126AD40, "8B54240833C0568B742408890689460489460889460CB802000000668906"),
        (0x0127F200, "A120D4F901508D4C243851E830BBFEFF8BF88B570483C42052FF15A8689F01"),
        (0x0127F22E, "55FF15A4689F018B2DA0689F016A0089869C010000FFD56689869A010000"),
        (0x00D91F06, "688CBEBD01895C2464E80C3173FF50E8964B73FF"),
        (0x00D91F73, "A3EC6B0602C705F06B060201000000"),
        (0x00D96107, "8B0DEC6B06028D6E1C894E18"),
        (0x00D99415, "8B0DEC6B0602894E18"),
        (0x00D99543, "833DF06B0602007542E88F85FFFF"),
        (0x00D9956D, "8B15EC6B060268286C06028BCF895618"),
        (0x00D996C8, "8B4718894618EB09"),
        (0x012879F5, "5357556A008BCEE81F76FFFF"),
        (0x0127F24C, "85DB7511"),
        (0x0127F257, "50FFD56689869A010000"),
        (0x0127F26B, "8B8EA80100006A010FB7F86A148D86980100005051E82BBAFEFF83C41085C0"),
        (0x0126ACD0, "8B4C24148B018B500C56FFD283F8017431"),
        (0x0126ACE1, "0FB7460250FFD36685C0741E0FB74E0251FFD30FB7C003C550FF15A0689F01473B7C241C668946027CC5"),
        (0x0126E480, "8B4424048B49146A105051FF1594689F01F7D81BC040C20400"),
        (0x01CD421C, "80E42601")
    ];

    private void VerifyListenPortAnchors()
    {
        foreach (var anchor in ListenPortAnchors) Verify(anchor.Address, Convert.FromHexString(anchor.Original));
    }

    /// <summary>
    /// Sets the actual FURL.Port and loopback bind address in the verified listen branch, preserving the
    /// connect branch.
    /// </summary>
    private void InstallListenPortHook()
    {
        Require(Workspace.TargetPort is >= 1024 and <= 65535, "本次 AFDEV listen 端口超出有效范围。");
        VerifyListenPortAnchors();
        Verify(ListenPortRead, Convert.FromHexString(ListenPortReadOriginal));

        listenPortTrace = Process.AllocateMemory(4096);

        uint trampoline = listenPortTrace + 0x100;
        var code = new X86Code();

        // InitListen -> InitBase(false, ..., FURL&, ...): ECX reads URL from the original stack slot;
        // ESI holds the actual NetDriver.
        // Write +18 only at the live FURL call site immediately before htons/bind, without changing the global default Port.
        // Original instructions change only ECX/EAX. Added MOV leaves flags unchanged; counter INC saves/restores flags separately, preserving stack balance.
        code.Store(listenPortTrace, 0).Hex(ListenPortReadOriginal);
        code.Byte(0xA3).UInt32(listenPortTrace + 16);
        code.Hex("890D").UInt32(listenPortTrace + 4).Hex("8935").UInt32(listenPortTrace + 8);
        code.Hex("C74118").UInt32((uint)Workspace.TargetPort).Hex("8B4118");
        // 0127F23D writes sin_addr at driver+19C; 0127F278 passes the same driver+198 sockaddr
        // to BindNextPort. Override only this listen address, then let native bind consume it; MOV preserves flags.
        code.Hex("C7869C010000").UInt32(0x0100007F);
        code.Byte(0xA3).UInt32(listenPortTrace + 12);
        code.Hex("9CFF05").UInt32(listenPortTrace + 20).Byte(0x9D).Store(listenPortTrace, 1);
        code.Jump(trampoline, ListenPortRead + 7);
        Install(trampoline, code.ToArray());
        Install(ListenPortRead, new X86Code().Jump(ListenPortRead, trampoline).Hex("9090").ToArray());
        log($"NATIVE_LISTEN_PORT_HOOK target_port={Workspace.TargetPort} address=0x{ListenPortRead:X8}");
    }

    /// <summary>
    /// Resets on the game thread with the OPEN milestone, excluding frontend-world or earlier listen-call
    /// evidence.
    /// </summary>
    private void AppendListenPortTraceReset(X86Code code)
    {
        for (uint offset = 0; offset < 24; offset += 4) code.Store(listenPortTrace + offset, 0);
    }

    /// <summary>
    /// Checks FURL consumption recorded for this OPEN. Independently verify the actual bind through
    /// getsockname and OWNER_PID for the same socket.
    /// </summary>
    internal void VerifyListenPortInitialization(uint driver)
    {
        byte[] snapshot = Process.ReadMemory(listenPortTrace, 24);
        uint complete = BinaryPrimitives.ReadUInt32LittleEndian(snapshot);
        uint url = BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(4));
        uint capturedDriver = BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(8));
        uint port = BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(12));
        uint originalPort = BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(16));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(20));

        Require(complete == 1 && count > 0 && url >= 0x10000 && (url & 3) == 0 &&
            capturedDriver == driver && port == Workspace.TargetPort &&
            ReadUInt32(driver + 0x19C) == 0x0100007F &&
            Process.ReadMemory(listenPortTrace, 24).AsSpan().SequenceEqual(snapshot),
            "当前 NetDriver 没有本次 OPEN 的目标 FURL.Port 初始化证据。");

        // FURL may have left its stack frame. Use values captured during the call; never dereference the old URL pointer afterward.
        if (reportedListenPort) return;

        reportedListenPort = true;
        log($"NATIVE_LISTEN_PORT_APPLIED driver=0x{driver:X8} url=0x{url:X8} " +
            $"original_port={originalPort} target_port={port} bind_address=127.0.0.1 post_open_calls={count}");
    }
}
