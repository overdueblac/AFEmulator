using System.Security.Cryptography;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// A one-time secret for the compatibility association channel. Deliver only to the authenticated
/// connection that created the grant; never log or broadcast it to the room. This type has no
/// original-client wire encoding. The trusted transport adapter should dispose it after sending.
/// </summary>
public sealed class MatchAdmissionTicket : IDisposable
{
    private readonly object gate = new();
    private byte[]? secret;

    internal MatchAdmissionTicket(Guid allocationId, Guid admissionId, byte[] secret)
    {
        AllocationId = allocationId;
        AdmissionId = admissionId;
        this.secret = secret;
    }

    /// <summary>
    /// The current server-allocation ID. It is not an authentication secret and cannot replace the round
    /// object retained by the host.
    /// </summary>
    public Guid AllocationId { get; }

    /// <summary>
    /// Locates a candidate grant within this round; its independent secret still requires validation.
    /// </summary>
    public Guid AdmissionId { get; }

    /// <summary>Copies the secret for transmission. The caller must zero the copy after use.</summary>
    public byte[] CopySecret()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(secret is null, this);
            return (byte[])secret.Clone();
        }
    }

    /// <summary>Clears this object's secret without replacing server-side grant revocation.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (secret is null) return;
            CryptographicOperations.ZeroMemory(secret);
            secret = null;
        }
    }
}
