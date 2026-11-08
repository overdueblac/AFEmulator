using System.Security.Cryptography;

namespace AFServer.AssaultFire.Protocol;

/// <summary>
/// Fixed input hashes from the original Python implementation, covering both padding branches and
/// whole-block boundaries.
/// </summary>
public static class ProtocolCompatibilityChecks
{
    public static void Run()
    {
        // Plaintext is 00..(n-1), key 00..0f, with random padding fixed to zero.
        (int Length, string Ap, string Mode3, string Mode4)[] vectors =
        [
            (0, "", "c0653f4033bb157c78a17a00297b6ea6bab67a23c339a876e9ab0ec9a63f854a", "e4f21a5fe2312dfe9a1ed7887ffc5d1cb0a7b296827c5ccb3e04c1a615c4c43f"),
            (1, "3fbecbba30647083738224913fda00515fbcc71278b381255aebbfa3162b841e", "e27060eaf605aa24f35f9ad216be2fa1ca1a7c00b8a0aa6889a2087d6c18e2fb", "5b52f25cb90289861e0d3338919e930a84cf621893cff93610be08c2279b9444"),
            (10, "904e2718179bd9859408ae225b1c49dc96bef5e09993a7c0036ef07f7a322e49", "b7472edd163b966b110470778be58f263cfdc2105bb0ff39113c557987a8503f", "b99c08b5d41bd46cbd0e7a38fa98d54e9418e31bfb187c7ee5b257f2a970171f"),
            (11, "0aed8d5572c77438bf63657f5ad22af26f92b1d40a0119eef80399d7b24486b3", "5fe770dc8f5dcd8eb857730ced21ff4e98151dcdad264faf7d16aa6a043d83a2", "290fe19348e1951eebadca07b41cf2fcf0c675b0aee0ff61082949fc01a56127"),
            (16, "320b3530c3eaef6c220471e69b095a2ba66ad0983d52d6acf2ebccb24fc3e079", "eb07f7911df10b4831b9078818c7873bd227440eb957c01ff5cd3c1bf006231e", "e61b0875dee44154979dd5dd4a802400ee0cd2a683271d69eb907a6553e01a6b"),
            (17, "5cc320e498bc65030f0527b8c2ec804279730f1fb947af204b2da64e6bf906e9", "63fb8ea718e95ce1e37aeecd1ae99796b875898f33753cbe8eb59735c151dee6", "f891e2d57d229713ae4c3f58ca9151d7189b52fcdd2c8582a050066fdf874b8a"),
            (31, "285b0ac728c0d1659139ecbb2b7cbc549be55fe9b98c3850eddbe7f35d9aff77", "24c0449056e623ef18cfdb11dbe8206b26569d375730b17a08cc61a59a8584a5", "669e8e8e30fa2eb951aa3ec457a05dcf781c4a0bc110e5941994fdbbc9c73874"),
            (32, "792ab838f84c493adcfe293f271f3f4b56838c9949ad363b785b8addaa86e85a", "9d0496fb0f62c3936240da1a1ce9273bae2d8323cbeefe680662f4f88c7f893b", "ace6dbffa2bac4b2a2659b343dbc3dd9b7fa1b9c9360c054369785d351d610e2"),
            (255, "25069344b04f7c7b6a570bfd7f11176d25726cafc26c6495523e5f7a25fa6edc", "54588f92f0755af17c1e1fff88ea80914399e023cb75ac6572b0d6831c9241af", "2c8c9784498612dddd0b7aeb5e185bf6849541f6e85ff9535754599ea209e6fe")
        ];
        byte[] key = Enumerable.Range(0, 16).Select(x => (byte)x).ToArray();

        foreach (var vector in vectors)
        {
            byte[] plain = Enumerable.Range(0, vector.Length).Select(x => (byte)x).ToArray();

            foreach (int mode in new[] { 3, 4 })
            {
                byte[] cipher = LegacyCrypto.Encrypt(plain, key, mode, deterministic: true);

                VerifyHash(cipher, mode == 3 ? vector.Mode3 : vector.Mode4);
                if (!LegacyCrypto.Decrypt(cipher, key, mode).SequenceEqual(plain)) throw new InvalidDataException("传输解密向量失败。");
            }
            if (vector.Length > 0)
            {
                byte[] cipher = LegacyCrypto.ApEncrypt(key, plain);

                VerifyHash(cipher, vector.Ap);
                if (!LegacyCrypto.ApDecrypt(key, cipher).SequenceEqual(plain)) throw new InvalidDataException("AP 解密向量失败。");
            }
        }
    }

    private static void VerifyHash(byte[] actual, string expected)
    {
        if (!SHA256.HashData(actual).SequenceEqual(Convert.FromHexString(expected))) throw new InvalidDataException("Python 协议兼容向量不匹配。");
    }
}
