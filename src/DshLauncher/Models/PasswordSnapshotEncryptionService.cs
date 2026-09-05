using System.Buffers.Binary;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DshLauncher.Models;

/// <summary>
/// Encrypts the ZIP payload used by VersionSnapshotService for transfer
/// between computers. The container is deliberately a small binary format so
/// its version, KDF parameters and AES-GCM framing can be validated before
/// any restore work starts.
/// </summary>
public sealed class PasswordSnapshotEncryptionService
{
    public const string FileExtension = ".dshpsnapshot";
    public const int CurrentSchemaVersion = 1;
    public const string KdfAlgorithm = "PBKDF2-SHA256";
    public const int DefaultPbkdf2Iterations = 600_000;
    public const int SaltSizeBytes = 16;
    public const int KeySizeBytes = 32;
    public const int NonceSizeBytes = 12;
    public const int TagSizeBytes = 16;
    public const int MaximumPbkdf2Iterations = 2_000_000;
    public const int MinimumPbkdf2Iterations = 100_000;
    public const int MaximumPlaintextBytes = 64 * 1024 * 1024;

    private const byte KdfAlgorithmId = 1;
    private const int MagicSizeBytes = 8;
    private const int HeaderSizeBytes = MagicSizeBytes + 2 + (sizeof(int) * 6);
    private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes("DSHPSNAP");

    public static ReadOnlySpan<byte> Magic => MagicBytes;

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return Encrypt(plaintext, password.AsSpan());
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<char> password)
    {
        EnsurePassword(password);
        if (plaintext.Length > MaximumPlaintextBytes)
        {
            throw new InvalidDataException("跨电脑密码快照超过 64 MiB 安全上限。 ");
        }

        var salt = new byte[SaltSizeBytes];
        var nonce = new byte[NonceSizeBytes];
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);

        var header = CreateHeader(ciphertext.Length, DefaultPbkdf2Iterations);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            DefaultPbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeySizeBytes);
        try
        {
            using var aes = new AesGcm(key, TagSizeBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, header);
            return Combine(header, salt, nonce, tag, ciphertext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public byte[] Decrypt(ReadOnlySpan<byte> container, string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return Decrypt(container, password.AsSpan());
    }

    public byte[] Decrypt(ReadOnlySpan<byte> container, ReadOnlySpan<char> password)
    {
        EnsurePassword(password);
        var envelope = Parse(container);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password,
            envelope.Salt,
            envelope.Header.Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            envelope.Header.KeySizeBytes);
        var plaintext = new byte[envelope.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, envelope.Header.TagSizeBytes);
            aes.Decrypt(
                envelope.Nonce,
                envelope.Ciphertext,
                envelope.Tag,
                plaintext,
                BuildHeader(envelope.Header));
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public PasswordSnapshotEnvelope Parse(ReadOnlySpan<byte> container)
    {
        if (container.Length < HeaderSizeBytes)
        {
            throw new InvalidDataException("跨电脑密码快照头部不完整。 ");
        }

        if (!container[..MagicSizeBytes].SequenceEqual(MagicBytes))
        {
            throw new InvalidDataException("不是有效的跨电脑密码快照。 ");
        }

        var offset = MagicSizeBytes;
        var schemaVersion = container[offset++];
        var kdfAlgorithmId = container[offset++];
        if (schemaVersion != CurrentSchemaVersion || kdfAlgorithmId != KdfAlgorithmId)
        {
            throw new InvalidDataException("跨电脑密码快照版本或 KDF 算法不受支持。 ");
        }

        var iterations = ReadInt32(container, ref offset);
        var saltSize = ReadInt32(container, ref offset);
        var keySize = ReadInt32(container, ref offset);
        var nonceSize = ReadInt32(container, ref offset);
        var tagSize = ReadInt32(container, ref offset);
        var ciphertextSize = ReadInt32(container, ref offset);
        if (iterations is < MinimumPbkdf2Iterations or > MaximumPbkdf2Iterations
            || saltSize != SaltSizeBytes
            || keySize != KeySizeBytes
            || nonceSize != NonceSizeBytes
            || tagSize != TagSizeBytes
            || ciphertextSize < 0
            || ciphertextSize > MaximumPlaintextBytes)
        {
            throw new InvalidDataException("跨电脑密码快照 KDF 参数或长度无效。 ");
        }

        var expectedLength = (long)HeaderSizeBytes + saltSize + nonceSize + tagSize + ciphertextSize;
        if (expectedLength != container.Length)
        {
            throw new InvalidDataException("跨电脑密码快照长度与头部不一致。 ");
        }

        var salt = container.Slice(HeaderSizeBytes, saltSize).ToArray();
        var nonce = container.Slice(HeaderSizeBytes + saltSize, nonceSize).ToArray();
        var tag = container.Slice(HeaderSizeBytes + saltSize + nonceSize, tagSize).ToArray();
        var ciphertext = container.Slice(
            HeaderSizeBytes + saltSize + nonceSize + tagSize,
            ciphertextSize).ToArray();
        return new PasswordSnapshotEnvelope(
            new PasswordSnapshotHeader(
                schemaVersion,
                KdfAlgorithm,
                iterations,
                saltSize,
                keySize,
                nonceSize,
                tagSize,
                ciphertextSize),
            salt,
            nonce,
            tag,
            ciphertext);
    }

    private static byte[] CreateHeader(int ciphertextSize, int iterations) =>
        BuildHeader(new PasswordSnapshotHeader(
            CurrentSchemaVersion,
            KdfAlgorithm,
            iterations,
            SaltSizeBytes,
            KeySizeBytes,
            NonceSizeBytes,
            TagSizeBytes,
            ciphertextSize));

    private static byte[] BuildHeader(PasswordSnapshotHeader header)
    {
        var bytes = new byte[HeaderSizeBytes];
        MagicBytes.AsSpan().CopyTo(bytes);
        var offset = MagicSizeBytes;
        bytes[offset++] = checked((byte)header.SchemaVersion);
        bytes[offset++] = KdfAlgorithmId;
        WriteInt32(bytes, ref offset, header.Pbkdf2Iterations);
        WriteInt32(bytes, ref offset, header.SaltSizeBytes);
        WriteInt32(bytes, ref offset, header.KeySizeBytes);
        WriteInt32(bytes, ref offset, header.NonceSizeBytes);
        WriteInt32(bytes, ref offset, header.TagSizeBytes);
        WriteInt32(bytes, ref offset, header.CiphertextSizeBytes);
        return bytes;
    }

    private static byte[] Combine(params byte[][] segments)
    {
        var totalLength = segments.Sum(segment => segment.Length);
        var result = new byte[totalLength];
        var offset = 0;
        foreach (var segment in segments)
        {
            segment.AsSpan().CopyTo(result.AsSpan(offset));
            offset += segment.Length;
        }

        return result;
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, sizeof(int)));
        offset += sizeof(int);
        return value;
    }

    private static void WriteInt32(byte[] bytes, ref int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, sizeof(int)), value);
        offset += sizeof(int);
    }

    private static void EnsurePassword(ReadOnlySpan<char> password)
    {
        if (password.IsEmpty)
        {
            throw new ArgumentException("快照密码不能为空。", nameof(password));
        }
    }
}
