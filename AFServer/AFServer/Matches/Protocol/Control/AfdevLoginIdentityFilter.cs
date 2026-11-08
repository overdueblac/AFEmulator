using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace AFServer.AssaultFire.Matches.Protocol;

/// <summary>
/// Constrains native login-URL identity for a specified lease, transforming only verified NMT_Login
/// Name/Uin fields. Zero-key is this AFDEV build's transport setting, not player authentication. Supply
/// authenticated identity/nickname from the frozen lobby snapshot.
/// </summary>
/// <remarks>
/// One relay calls serially. ACKs, noncontrol bunches, and unchanged datagrams retain original bytes;
/// unknown control messages fail.
/// </remarks>
public sealed class AfdevLoginIdentityFilter
{
    private const int MaximumPlaintextBytes = 65488;
    private readonly string authenticatedUin;
    private readonly string nickname;
    private readonly byte? camp;
    private readonly bool observer;
    private string? loginUrl;

    /// <summary>
    /// Creates a filter for an exact redeemed session without querying an online-UIN table or second
    /// business database.
    /// </summary>
    public AfdevLoginIdentityFilter(ulong authenticatedUin, string nickname, byte? camp = null, bool observer = false)
    {
        if (authenticatedUin == 0)
            throw new ArgumentOutOfRangeException(nameof(authenticatedUin));
        if (string.IsNullOrWhiteSpace(nickname) || nickname.Length > 31 ||
            nickname.Any(value => char.IsControl(value) || value == '?'))
            throw new ArgumentException("原生登录昵称必须来自有效的冻结角色快照。", nameof(nickname));

        this.authenticatedUin = authenticatedUin.ToString(CultureInfo.InvariantCulture);
        this.nickname = nickname;
        if (camp > 1) throw new ArgumentOutOfRangeException(nameof(camp));

        this.camp = camp;
        this.observer = observer;
    }

    /// <summary>
    /// A login URL was observed and validated; this does not prove native PlayerController/Pawn creation.
    /// </summary>
    public bool LoginObserved => loginUrl != null;

    /// <summary>
    /// Join observed after identity checks. Preserve the eight-byte OpaqueId verbatim; it is not an
    /// authentication credential.
    /// </summary>
    public bool JoinObserved { get; private set; }

    /// <summary>
    /// Updates login observation only after the full packet passes. Missing/wrong identity, ambiguous URLs,
    /// or unknown control fields fail closed.
    /// </summary>
    public byte[] Inspect(ReadOnlySpan<byte> datagram)
    {
        byte[] plaintext = Decrypt(datagram);

        try
        {
            var packet = Ue3PacketCodec.Decode(plaintext, MaximumPlaintextBytes);
            var records = new List<Ue3Record>(packet.Records.Count);
            string? acceptedUrl = loginUrl;
            bool joined = JoinObserved;
            bool changed = false;

            foreach (var record in packet.Records)
            {
                if (record is Ue3Bunch { ChannelIndex: not 0, ChannelType: 1 })
                    throw new InvalidDataException("原生控制通道只能为零号，拒绝其他通道承载登录身份。");

                if (record is not Ue3Bunch { ChannelIndex: 0 } bunch)
                {
                    records.Add(record);

                    continue;
                }

                if (!bunch.Reliable || bunch.ChannelType != 1 || (bunch.PayloadBitLength & 7) != 0)
                    throw new InvalidDataException("原生登录控制通道不是已核实的可靠字节流。");

                var control = Ue3ControlCodec.DecodeKnownStream(bunch.Payload);

                if (control.Status != Ue3ControlDecodeStatus.Complete)
                    throw new InvalidDataException("原生登录控制消息含未知边界，拒绝绕过身份检查。");

                using var output = new MemoryStream();
                int position = 0;

                foreach (var message in control.Messages)
                {
                    int start = position;
                    byte type = bunch.Payload[position++];

                    switch (message)
                    {
                        case Ue3Login:
                            // Original PE server NMT5 -> 00D7FF40 consumes only two FStrings. Copy other NMTs unchanged.
                            FStringCodec.Read(bunch.Payload, ref position);

                            int urlStart = position;
                            bool wide = BinaryPrimitives.ReadInt32LittleEndian(bunch.Payload.AsSpan(position)) < 0;
                            string suppliedUrl = FStringCodec.Read(bunch.Payload, ref position);
                            string normalized = NormalizeUrl(suppliedUrl);

                            if (acceptedUrl != null && acceptedUrl != normalized)
                                throw new InvalidDataException("同一 lease 的重复登录改变了 URL，必须开始新回合。");

                            acceptedUrl = normalized;
                            output.Write(bunch.Payload.AsSpan(start, urlStart - start));

                            if (normalized == suppliedUrl)
                                output.Write(bunch.Payload.AsSpan(urlStart, position - urlStart));
                            else
                            {
                                output.Write(FStringCodec.Write(normalized, wide || normalized.Any(value => value > 127)));
                                changed = true;
                            }
                            break;
                        case Ue3Hello:
                            position += 9;
                            output.Write(bunch.Payload.AsSpan(start, position - start));
                            break;
                        case Ue3Netspeed:
                            position += 4;
                            output.Write(bunch.Payload.AsSpan(start, position - start));
                            break;
                        case Ue3PackageHave:
                            position += 20;
                            output.Write(bunch.Payload.AsSpan(start, position - start));
                            break;
                        case Ue3PackageSkip or Ue3PackageAbort:
                            position += 16;
                            output.Write(bunch.Payload.AsSpan(start, position - start));
                            break;
                        case Ue3Join:
                            if (acceptedUrl == null)
                                throw new InvalidDataException("没有当前 lease 的已核实 NMT_Login，拒绝 NMT_Join。");

                            position += 8;
                            joined = true;
                            output.Write(bunch.Payload.AsSpan(start, position - start));
                            break;
                        default:
                            throw new InvalidDataException($"客户端发送未核实方向的控制消息 {type}。");
                    }
                }

                byte[] payload = output.ToArray();

                if (payload.Length * 8 >= Ue3PacketCodec.PayloadBitLengthMaximum)
                    throw new InvalidDataException("登录规范化后的控制 bunch 超出原生 12 位长度。");

                records.Add(bunch with { Payload = payload, PayloadBitLength = payload.Length * 8 });
            }

            byte[] result = changed
                ? Encrypt(Ue3PacketCodec.Encode(packet with { Records = records }, MaximumPlaintextBytes))
                : datagram.ToArray();

            loginUrl = acceptedUrl;
            JoinObserved = joined;

            return result;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private string NormalizeUrl(string value)
    {
        if (value.Any(char.IsControl))
            throw new InvalidDataException("原生登录 URL 含控制字符。");

        string[] parts = value.Split('?');
        int uinCount = 0;
        int nameCount = 0;
        int teamCount = 0;
        int spectatorCount = 0;
        int observerCount = 0;

        for (int index = 1; index < parts.Length; index++)
        {
            string option = parts[index];
            int separator = option.IndexOf('=');
            string key = separator < 0 ? option : option[..separator];

            if (key.Equals("Uin", StringComparison.OrdinalIgnoreCase))
            {
                string supplied = separator < 0 ? "" : option[(separator + 1)..];

                if (++uinCount != 1 || supplied != authenticatedUin)
                    throw new InvalidDataException("NMT_Login UIN 与当前已认证 lease 不一致或重复。");

                parts[index] = "Uin=" + authenticatedUin;
            }
            if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                if (++nameCount != 1)
                    throw new InvalidDataException("NMT_Login 含重复 Name。");

                parts[index] = "Name=" + nickname;
            }
            if (camp is not null && key.Equals("Team", StringComparison.OrdinalIgnoreCase))
            {
                if (++teamCount != 1) throw new InvalidDataException("NMT_Login 含重复 Team。");

                parts[index] = "Team=" + camp.Value.ToString(CultureInfo.InvariantCulture);
            }
            if (camp is not null && key.Equals("SpectatorOnly", StringComparison.OrdinalIgnoreCase))
            {
                if (++spectatorCount != 1) throw new InvalidDataException("NMT_Login 含重复 SpectatorOnly。");

                parts[index] = "SpectatorOnly=" + (observer ? "1" : "0");
            }
            if (camp is not null && key.Equals("Observe", StringComparison.OrdinalIgnoreCase))
            {
                if (++observerCount != 1) throw new InvalidDataException("NMT_Login 含重复 Observe。");

                parts[index] = "Observe=" + (observer ? "1" : "0");
            }
        }

        if (uinCount != 1)
            throw new InvalidDataException("NMT_Login 缺少当前已认证玩家的唯一 UIN。");

        return string.Join('?', parts) + (nameCount == 0 ? "?Name=" + nickname : "") +
            (camp is not null && teamCount == 0 ? "?Team=" + camp.Value.ToString(CultureInfo.InvariantCulture) : "") +
            (camp is not null && spectatorCount == 0 ? "?SpectatorOnly=" + (observer ? "1" : "0") : "") +
            (camp is not null && observerCount == 0 ? "?Observe=" + (observer ? "1" : "0") : "");
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < 20 || datagram.Length > 65507 || ((datagram.Length - 4) & 15) != 0)
            throw new InvalidDataException("AFDEV 数据报不符合已核实的 AES 外层。");

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(datagram);

        if (length is < 2 or > MaximumPlaintextBytes || ((length + 15) & ~15U) != datagram.Length - 4)
            throw new InvalidDataException("AFDEV 数据报声明长度与密文不一致。");

        using var aes = Aes.Create();

        aes.Key = new byte[16];

        byte[] padded = aes.DecryptEcb(datagram[4..], PaddingMode.None);

        try { return padded.AsSpan(0, (int)length).ToArray(); }
        finally { CryptographicOperations.ZeroMemory(padded); }
    }

    private static byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        byte[] padded = new byte[(plaintext.Length + 15) & ~15];
        byte[] result = new byte[4 + padded.Length];

        plaintext.CopyTo(padded);
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)plaintext.Length);

        using var aes = Aes.Create();

        aes.Key = new byte[16];

        try { aes.EncryptEcb(padded, result.AsSpan(4), PaddingMode.None); }
        finally { CryptographicOperations.ZeroMemory(padded); }

        return result;
    }
}
