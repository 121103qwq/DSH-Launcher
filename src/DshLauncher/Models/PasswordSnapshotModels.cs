using System;

namespace DshLauncher.Models;

/// <summary>
/// Header values stored in a password-protected cross-machine snapshot.
/// None of these values contain the password or decrypted snapshot content.
/// </summary>
public sealed record PasswordSnapshotHeader(
    int SchemaVersion,
    string KdfAlgorithm,
    int Pbkdf2Iterations,
    int SaltSizeBytes,
    int KeySizeBytes,
    int NonceSizeBytes,
    int TagSizeBytes,
    int CiphertextSizeBytes);

/// <summary>
/// Parsed cross-machine snapshot envelope. The byte arrays are transient and
/// must not be persisted outside the encrypted container implementation.
/// </summary>
public sealed record PasswordSnapshotEnvelope(
    PasswordSnapshotHeader Header,
    byte[] Salt,
    byte[] Nonce,
    byte[] Tag,
    byte[] Ciphertext);

public sealed record PasswordSnapshotInfo(
    string FilePath,
    DateTimeOffset CreatedAt,
    long Size);
