using System.Security.Cryptography;

namespace ReTran.Tests.Update;

/// <summary>
/// Test-only ECDSA P-256 keypair — generated at runtime, never touches the production key.
/// Keypair ECDSA P-256 test-only — sinh runtime, không đụng key production.
/// </summary>
internal sealed class TestSigningKey : IDisposable
{
    private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>
    /// SPKI Base64 of the public half of this ephemeral keypair.
    /// SPKI Base64 của phần public của keypair tạm này.
    /// </summary>
    public string PublicKeyBase64 { get; }

    public TestSigningKey()
    {
        PublicKeyBase64 = Convert.ToBase64String(_ecdsa.ExportSubjectPublicKeyInfo());
    }

    /// <summary>
    /// Signs the SHA-256 hash of <paramref name="data"/> and returns Base64 (the release .sig format).
    /// Ký SHA-256 của <paramref name="data"/>, trả Base64 (định dạng .sig trên release).
    /// </summary>
    public string SignToBase64(byte[] data)
    {
        var hash = SHA256.HashData(data);
        // ECDsa mặc định xuất IEEE P1363 (r||s 64 bytes với P-256) — trùng format CI ký
        return Convert.ToBase64String(_ecdsa.SignHash(hash));
    }

    public void Dispose() => _ecdsa.Dispose();
}
