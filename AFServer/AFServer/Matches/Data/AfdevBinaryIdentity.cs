namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Hashes of the AFDEV file and actual relocated ServerMove v4 function body from the same read-only
/// validation.
/// </summary>
/// <param name="AfdevSha256">Lowercase hexadecimal SHA-256 of the complete AFDEV file.</param>
/// <param name="ServerMoveV4Sha256">
/// Lowercase hexadecimal SHA-256 of the 639-byte body in the actual .afm4 section.
/// </param>
public sealed record AfdevBinaryIdentity(string AfdevSha256, string ServerMoveV4Sha256);
