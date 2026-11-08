using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// TDR application codecs. Upstream packets have a four-byte sequence prefix; downstream packets start with
/// the magic number. Never interchange them.
/// </summary>
internal static class GameWire
{
    internal const ushort ZoneMagic = 0x3243;
    internal const ushort GeoMagic = 0x8202;
    internal const ushort ZoneSuccess = 0x8100;
    internal const ushort GeoSuccess = 0x8B00;
    internal const int AchievementSuccess = 0x8A00;
    internal const int AchievementWrongData = 0x0A03;

    /// <summary>
    /// Maps common requests to stable business names while retaining hexadecimal IDs for unknown commands.
    /// </summary>
    internal static string Describe(ushort magic, ushort command) => (magic, command) switch
    {
        (ZoneMagic, 0xA000) => "登录",
        (ZoneMagic, 0xA001) => "登录响应",
        (ZoneMagic, 0xA002) => "创建角色",
        (ZoneMagic, 0xA003) => "创建角色响应",
        (ZoneMagic, 0xA004) => "心跳/在线状态",
        (ZoneMagic, 0xA005) => "角色档案",
        (ZoneMagic, 0xA006) => "背包档案",
        (ZoneMagic, 0xA008) => "装备/卸下道具",
        (ZoneMagic, 0xA009) => "道具操作响应",
        (ZoneMagic, 0xA00A) => "道具操作/钱包通知",
        (ZoneMagic, 0xA100) => "查询房间",
        (ZoneMagic, 0xA102) => "房间列表",
        (ZoneMagic, 0xA104) => "进入房间",
        (ZoneMagic, 0xA105) => "房间详情",
        (ZoneMagic, 0xA106) => "房间成员",
        (ZoneMagic, 0xA107) => "离开房间",
        (ZoneMagic, 0xA108) => "离开房间响应",
        (ZoneMagic, 0xA10A) => "创建房间",
        (ZoneMagic, 0xA10B) => "创建房间响应",
        (ZoneMagic, 0xA10D) => "切换阵营",
        (ZoneMagic, 0xA10E) => "切换阵营响应",
        (ZoneMagic, 0xA10F) => "阵营变更通知",
        (ZoneMagic, 0xA110) => "设置准备",
        (ZoneMagic, 0xA111) => "设置准备响应",
        (ZoneMagic, 0xA112) => "准备状态通知",
        (ZoneMagic, 0xA113) => "开始对局",
        (ZoneMagic, 0xA114) => "开始对局响应",
        (ZoneMagic, 0xA117) => "退出对局",
        (ZoneMagic, 0xA118) => "退出对局应答",
        (ZoneMagic, 0xA119) => "玩家退局通知",
        (ZoneMagic, 0xA11B) => "对局结算",
        (ZoneMagic, 0xA11A) => "对局端点",
        (ZoneMagic, 0xA11C) => "加入对局",
        (ZoneMagic, 0xA11D) => "加入对局通知",
        (ZoneMagic, 0xA132) => "请求子频道",
        (ZoneMagic, 0xA133) => "子频道信息",
        (ZoneMagic, 0xA200) => "装备/卸下道具",
        (ZoneMagic, 0xA303) => "好友状态/大厅就绪标记",
        (ZoneMagic, 0xA325) => "设置名片隐私",
        (ZoneMagic, 0xA355) => "请求主频道",
        (ZoneMagic, 0xA356) => "主频道信息",
        (ZoneMagic, 0xA361) => "文件校验",
        (ZoneMagic, 0xA362) => "文件校验响应",
        (ZoneMagic, 0xA503) => "请求钱包",
        (ZoneMagic, 0xA504) => "钱包响应",
        (ZoneMagic, 0xA505) => "购买商品",
        (ZoneMagic, 0xA506) => "购买商品响应",
        (ZoneMagic, 0xA50E) => "请求资产",
        (ZoneMagic, 0xC001) => "查询分类成就",
        (ZoneMagic, 0xC002) => "分类成就详情",
        (ZoneMagic, 0xC003) => "查询成就与名片列表",
        (ZoneMagic, 0xC004) => "已达成成就列表",
        (ZoneMagic, 0xC006) => "设置名片",
        (ZoneMagic, 0xC007) => "设置名片响应",
        (ZoneMagic, 0xC008) => "查询成就统计",
        (ZoneMagic, 0xC009) => "成就统计与最近成就",
        (ZoneMagic, 0xC00A) => "查询成就分级历史",
        (ZoneMagic, 0xC00B) => "成就分级历史",
        (ZoneMagic, 0xC00C) => "成就触发检查",
        (ZoneMagic, 0xC00D) => "查询名片",
        (ZoneMagic, 0xC00E) => "名片资料",
        (ZoneMagic, 0xC00F) => "展示名片",
        (ZoneMagic, 0xC010) => "名片展示通知",
        (ZoneMagic, 0xC011) => "领取成就奖励",
        (ZoneMagic, 0xC012) => "成就奖励响应",
        (ZoneMagic, 0xFF05) => "请求角色档案",
        (ZoneMagic, 0xFF10) => "状态响应",
        (ZoneMagic, 0xFF13) => "区域提示通知",
        (GeoMagic, 0x1000) => "请求 GEO 地址",
        (GeoMagic, 0x1001) => "请求 GEO 状态",
        _ => "未知服务"
    };

    internal static bool TryReadOutgoing(ReadOnlySpan<byte> packet, out ushort magic, out ushort command, out int bodyLength)
    {
        magic = command = 0;
        bodyLength = 0;
        if (packet.Length < 8) return false;
        magic = BinaryPrimitives.ReadUInt16BigEndian(packet);
        command = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);

        ushort headerLength = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);

        bodyLength = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);
        return headerLength >= 8 && headerLength + bodyLength == packet.Length;
    }

    internal static ushort? ResponseHead(ReadOnlySpan<byte> packet)
    {
        if (!TryReadOutgoing(packet, out _, out _, out int bodyLength) || bodyLength < 2) return null;
        return BinaryPrimitives.ReadUInt16BigEndian(packet[8..]);
    }

    internal static (ushort Magic, ushort Command, byte[] Body) Parse(byte[] packet)
    {
        var reader = new TdrReader(packet);

        reader.U32();

        var magic = reader.U16();
        var command = reader.U16();
        var headerLength = reader.U16();
        var bodyLength = reader.U16();

        if (headerLength < 8 || 4 + headerLength + bodyLength != packet.Length)
            throw new InvalidDataException("TDR 应用包长度无效。");
        return (magic, command, packet.AsSpan(4 + headerLength, bodyLength).ToArray());
    }

    internal static byte[] Packet(ushort command, TdrWriter body, ushort magic = ZoneMagic)
    {
        var bytes = body.ToArray();

        if (bytes.Length > ushort.MaxValue) throw new InvalidDataException("TDR 应用包超过协议长度限制。");
        return new TdrWriter().U16(magic).U16(command).U16(8).U16(bytes.Length).Bytes(bytes).ToArray();
    }

    internal static uint Address(string address, bool hostOrder = true)
    {
        var bytes = IPAddress.Parse(address).GetAddressBytes();

        if (bytes.Length != 4) throw new InvalidDataException("旧客户端仅支持 IPv4 地址。");
        // TGame copies a host-order DWORD directly into sockaddr, while GEO PingInfo uses a network-semantic numeric value.
        return hostOrder ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }
}

internal sealed class TdrWriter
{
    private readonly MemoryStream stream = new();
    internal TdrWriter U8(int value) { stream.WriteByte(unchecked((byte)value)); return this; }
    internal TdrWriter U16(int value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, unchecked((ushort)value)); stream.Write(b); return this; }
    internal TdrWriter U32(long value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, unchecked((uint)value)); stream.Write(b); return this; }
    internal TdrWriter U64(ulong value) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, value); stream.Write(b); return this; }
    internal TdrWriter Double(double value) => U64(unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));
    internal TdrWriter Bytes(ReadOnlySpan<byte> value) { stream.Write(value); return this; }
    internal TdrWriter Zeros(int count) => Bytes(new byte[count]);
    internal TdrWriter String(string value, int maximum = 256)
    {
        var bytes = Encoding.UTF8.GetBytes(value);

        if (bytes.Length + 1 > maximum) throw new InvalidDataException($"文本超过协议限制：{maximum - 1} 字节。");
        return U32(bytes.Length + 1).Bytes(bytes).U8(0);
    }
    internal byte[] ToArray() => stream.ToArray();
}

internal sealed class TdrReader(byte[] bytes)
{
    internal int Position { get; private set; }
    internal int Remaining => bytes.Length - Position;
    internal byte U8() => Read(1)[0];
    internal ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Read(2));
    internal uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Read(4));
    internal ulong U64() => BinaryPrimitives.ReadUInt64BigEndian(Read(8));
    internal ReadOnlySpan<byte> Read(int count)
    {
        if (count < 0 || count > Remaining) throw new InvalidDataException("TDR 请求被截断。");

        var value = bytes.AsSpan(Position, count);

        Position += count;
        return value;
    }
    internal string String(int maximum)
    {
        var size = U32();

        if (size is 0 || size > maximum) throw new InvalidDataException("TDR 字符串长度无效。");

        var raw = Read((int)size);

        if (raw[^1] != 0) throw new InvalidDataException("TDR 字符串缺少终止符。");
        try { return new UTF8Encoding(false, true).GetString(raw[..^1]); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936).GetString(raw[..^1]);
        }
    }
    internal void End() { if (Remaining != 0) throw new InvalidDataException("TDR 请求包含未定义的尾部数据。"); }
}
