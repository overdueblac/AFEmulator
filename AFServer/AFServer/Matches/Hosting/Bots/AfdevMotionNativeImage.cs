namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Fingerprints of native physics, cancellation bodies, and audited parameter-decoding paths. Descriptor
/// validation neither starts nor advances bots.
/// </summary>
internal static class AfdevMotionNativeImage
{
    private static readonly (uint Address, int Length, string Hash)[] Spans =
    [
        (0x21e612c, 37, "9afedfe92c337ab2974dbe97939be023159b023119bd4ae47c07511e2b4c86bf"),
        (0x21e6152, 9, "5a5e98da875133ff4d580dbca0001ea7587f05f493ee9028ab18b632c02f812a"),
        (0x21ead7b, 163, "cac9d1de6c6f5e497f1650891ce1c55f5382370d819f35bc3df52178e57ea3cf"),
        (0x21fe2af, 41, "23a2d447b097392a11d65cb40113385ae56fb59334cb9043e764a9ebd8892943"),
        (0x21fe2d9, 8, "1dd2546443d78c87798afc5e92e618330bc13b9e8d122767fc5e26cec3c3e9e3"),
        (0x22203b5, 174, "8e71c7c354abacfa186607cb6df9f01a2530df591eacdfa70142f85445d09d81"),
        (0x2220464, 9, "7aa6b1e4d683bc7ac66ae37e06f32e85ecfe39f6b019ccf0d4e169c50923ef2e"),
        (0x22513eb, 32, "542690950f9846bfbb0d682c494cb72ab9633585f4db1a0e2a8f4745d6049b7e"),
        (0x229404b, 123, "b4644416e49850729603accb919d41615ac8c12836dbb6d7ea07c63119132f24"),
        (0x22ae5b8, 78, "520e34935870b2f47b859e7ceed49d738f7e5402b1bdcba70daa4f4cde328054"),
        (0xb95f00, 158, "f843d8fd59bc70cedc32107bade5cd6b8d6f793c1be520f91661161f97fcf8de"),
        (0x951790, 45, "9d8daa3b8ccce0dbfb70e928106f575c42b73d785abf364bfdb9061d6e972bd2"),
        (0x8eb870, 26, "dd3d79563e55d4e047aef0295545ee0bd0e32c634dfad40cef64160e6d506e46"),
        (0x1a51044, 4, "80c8a717ccd70c8809eb78e6a9591c003e11c721fe0ccaf62fd592abda1a5593"),
        (0x1a25e08, 4, "fb360f7241a770dc32cade9a1d1273c92b13bd4959b1c130931312b7fe95e63f"),
        (0x1a01648, 4, "c68830a25204a09f8e77aada6bc5807f607cccaaa0ebb2a7122d317584478a8b"),
        (0x1fa3580, 8, "76912ce20fdf8aae0ed9c05b15bb4abce4549a9e644d41b90206f391061a9153"),
        (0x1fa4b60, 8, "7376e4edaff6e032d35dbbf412f2057f95ee5da2936bb732838d1b4749757013"),
    ];

    public static void Verify(AfdevOwnedProcess process)
    {
        foreach (var span in Spans)
        {
            if (TickHook.Hash(process.ReadMemory(span.Address, span.Length)) != span.Hash)
                throw new InvalidDataException($"原生物理或取消入口发生变化：0x{span.Address:X8}。");
        }
    }
}
