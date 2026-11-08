using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;

namespace AFConnect.EDGE.Client;

/// <summary>
/// Implements the game's 32-tag TenProxy shared-memory protocol, with a lifetime tied to the exact newly
/// created player process.
/// </summary>
internal sealed class LauncherSharedMemory : IDisposable
{
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;

    internal LauncherSharedMemory(int processId, LauncherIdentity identity, LauncherServer server)
    {
        // Native assembly passes the full DWORD PID to %d. Preserve the protocol spelling; the decompiler's inferred char type must not truncate it.
        string name = "TCLS_SHAREDMEMEMORY" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        mapping = MemoryMappedFile.CreateNew(name, 0x80000, MemoryMappedFileAccess.ReadWrite);

        try
        {
            view = mapping.CreateViewAccessor(0, 0x80000, MemoryMappedFileAccess.ReadWrite);

            byte[] payload = Build(identity, server);

            try { view.WriteArray(0, payload, 0, payload.Length); }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }
        catch
        {
            view?.Dispose();
            mapping.Dispose();
            throw;
        }
    }

    internal static byte[] Build(LauncherIdentity identity, LauncherServer server)
    {
        var writer = new TagWriter();

        writer.Number(1, server.Id);
        writer.Number(2, identity.Uin);
        writer.Bytes(3, [checked((byte)identity.Ticket.Length)]);
        writer.Bytes(4, identity.Ticket);
        writer.Bytes(5, identity.Key);
        writer.Number(6, identity.Timestamp);
        writer.Bytes(7, []);
        writer.Text(14, Environment.ProcessPath ?? "");
        writer.Text(15, "");
        writer.Text(16, "");
        writer.Number(17, 1);
        writer.Number(18, 1);
        writer.Text(19, server.Name);
        writer.Bytes(26, [0]);
        writer.Bytes(27, []);
        // Server and Connect currently use the AP (AuthType 4) layout. The extended entry point takes the server ticket and requires both its actual length and data.
        // An empty extension is rejected locally. Omitting it falls back to the single-byte AuthType 3 ticket layout, which differs from the current connection protocol.
        writer.Number(30, checked((uint)identity.Ticket.Length));
        writer.Bytes(31, identity.Ticket);

        int table = writer.ReserveServerTable();

        writer.ServerText(table, 1, "");
        // TGame reads the connection host and port directly from LoginInfo +5248/+5504; these fields are not the directory version or server ID.
        writer.ServerText(table, 2, server.Host);
        writer.ServerNumber(table, 3, checked((uint)server.Port));
        writer.ServerText(table, 4, $"tcp://{server.Host}:{server.Port}");
        writer.ServerNumber(table, 5, 3);
        writer.ServerNumber(table, 6, 0);
        writer.ServerNumber(table, 7, 0);
        writer.ServerBytes(table, 8, []);
        writer.ServerText(table, 9, $"{server.Host}:{server.Port};");
        return writer.ToArray();
    }

    public void Dispose()
    {
        try
        {
            view.WriteArray(0, new byte[0x80000], 0, 0x80000);
        }
        finally
        {
            view.Dispose();
            mapping.Dispose();
        }
    }

    private sealed class TagWriter
    {
        private readonly MemoryStream stream = new();

        internal TagWriter()
        {
            stream.Write(new byte[132]);
            Set32(0, 32);
        }

        internal void Number(int tag, uint value) => Bytes(tag, BitConverter.GetBytes(value));
        internal void Text(int tag, string value) => Bytes(tag, Encoding.Unicode.GetBytes(value + '\0'));

        internal void Bytes(int tag, byte[] bytes)
        {
            Set32(tag * 4, checked((uint)stream.Position));
            Append(bytes);
        }

        internal int ReserveServerTable()
        {
            Set32(20 * 4, checked((uint)stream.Position));

            int groupTable = checked((int)stream.Position);

            stream.Write(new byte[4]);
            Set32(groupTable, checked((uint)stream.Position));

            int table = checked((int)stream.Position);

            stream.Write(new byte[64]);
            return table;
        }

        internal void ServerText(int table, int tag, string text) => ServerBytes(table, tag, Encoding.Unicode.GetBytes(text + '\0'));
        internal void ServerNumber(int table, int tag, uint value) => ServerBytes(table, tag, BitConverter.GetBytes(value));

        internal void ServerBytes(int table, int tag, byte[] bytes)
        {
            Set32(table + (tag - 1) * 4, checked((uint)stream.Position));
            Append(bytes);
        }

        internal byte[] ToArray() => stream.ToArray();

        private void Append(byte[] bytes)
        {
            Span<byte> length = stackalloc byte[4];

            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }

        private void Set32(int offset, uint value)
        {
            long previous = stream.Position;
            Span<byte> bytes = stackalloc byte[4];

            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            stream.Position = offset;
            stream.Write(bytes);
            stream.Position = previous;
        }
    }
}
