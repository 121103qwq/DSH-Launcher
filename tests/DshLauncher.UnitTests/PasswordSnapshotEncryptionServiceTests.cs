using System.Security.Cryptography;
using System.Text;
using DshLauncher.Models;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class PasswordSnapshotEncryptionServiceTests
{
    [Fact]
    public void EncryptDecryptRoundTripUsesAuthenticatedVersionedEnvelope()
    {
        var service = new PasswordSnapshotEncryptionService();
        var plaintext = Encoding.UTF8.GetBytes("portable snapshot payload\nwith unicode: 快照");

        var container = service.Encrypt(plaintext, "correct horse battery staple");
        var envelope = service.Parse(container);
        var restored = service.Decrypt(container, "correct horse battery staple");

        Assert.Equal(PasswordSnapshotEncryptionService.CurrentSchemaVersion, envelope.Header.SchemaVersion);
        Assert.Equal(PasswordSnapshotEncryptionService.KdfAlgorithm, envelope.Header.KdfAlgorithm);
        Assert.Equal(PasswordSnapshotEncryptionService.DefaultPbkdf2Iterations, envelope.Header.Pbkdf2Iterations);
        Assert.Equal(plaintext, restored);
        Assert.Equal(-1, container.AsSpan().IndexOf(plaintext));
    }

    [Fact]
    public void WrongPasswordAndTamperingAreRejected()
    {
        var service = new PasswordSnapshotEncryptionService();
        var container = service.Encrypt(Encoding.UTF8.GetBytes("authenticated payload"), "right-password");

        Assert.Throws<AuthenticationTagMismatchException>(() =>
            service.Decrypt(container, "wrong-password"));

        container[^1] ^= 0x01;
        Assert.Throws<AuthenticationTagMismatchException>(() =>
            service.Decrypt(container, "right-password"));
    }

    [Fact]
    public void EmptyPasswordAndMalformedEnvelopeAreRejectedBeforeRestore()
    {
        var service = new PasswordSnapshotEncryptionService();

        Assert.Throws<ArgumentException>(() => service.Encrypt([1, 2, 3], string.Empty));
        Assert.Throws<InvalidDataException>(() => service.Parse([1, 2, 3]));
    }
}
