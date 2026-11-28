using System.Security.Cryptography;

namespace ReTran_App.Services.Update;

/// <summary>
/// Verifies the ECDSA P-256 signature of an update zip downloaded from a GitHub Release.
/// Xác thực chữ ký ECDSA P-256 của zip update tải từ GitHub Release.
/// </summary>
/// <remarks>
/// The production public key is embedded in <see cref="ReleasePublicKeyBase64"/>; the private key
/// exists only in the GitHub Actions secret UPDATE_SIGNING_KEY (signing on CI) — never committed to the repo.
/// Public key production được nhúng tại <see cref="ReleasePublicKeyBase64"/>; private key
/// chỉ tồn tại trong GitHub Actions secret UPDATE_SIGNING_KEY (ký trên CI) — không commit vào repo.
/// The signature file (.sig) holds the Base64 of an IEEE P1363 signature (r||s, 64 bytes for P-256)
/// over the SHA-256 hash of the zip — CI uses the same format (see release.yml).
/// File chữ ký (.sig) chứa Base64 của signature IEEE P1363 (r||s, 64 bytes với P-256),
/// ký trên SHA-256 hash của zip — CI dùng cùng định dạng (xem release.yml).
/// Every case that cannot be authenticated returns <see langword="false"/> (fail-closed).
/// Mọi trường hợp không xác thực được đều trả về <see langword="false"/> (fail-closed).
/// </remarks>
public static class PackageSignatureVerifier
{
    /// <summary>
    /// ECDSA P-256 release signing public key (SPKI Base64).
    /// ECDSA P-256 public key (SPKI Base64) của release signing keypair.
    /// </summary>
    public const string ReleasePublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE9kK+aiaICKBJB/8i5L25h9Xa1xI6uMUgN65vVjVdrzkQAqYxn7iUzZEdU8HenhB+KtNY5oftByoQ8Lgtla2eAw==";

    /// <summary>
    /// Checks the ECDSA signature of file <paramref name="packagePath"/> stored in file <paramref name="signaturePath"/>.
    /// Kiểm tra chữ ký ECDSA của file <paramref name="packagePath"/> trong file <paramref name="signaturePath"/>.
    /// </summary>
    /// <param name="packagePath">Path of the update zip. Đường dẫn zip update.</param>
    /// <param name="signaturePath">Path of the signature file (.sig, Base64). Đường dẫn file chữ ký (.sig, Base64).</param>
    /// <param name="publicKeyBase64">SPKI Base64 of the public key to verify against. SPKI Base64 của public key cần verify.</param>
    /// <returns>
    /// <see langword="true"/> when the signature is valid; otherwise <see langword="false"/>.
    /// <see langword="true"/> khi chữ ký hợp lệ; ngược lại <see langword="false"/>.
    /// </returns>
    public static bool Verify(string packagePath, string signaturePath, string publicKeyBase64)
    {
        try
        {
            if (!File.Exists(packagePath) || !File.Exists(signaturePath))
            {
                return false;
            }

            var signature = Convert.FromBase64String(ReadTrimmedBase64(signaturePath));
            var hash = SHA256.HashData(File.ReadAllBytes(packagePath));

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out var bytesRead);
            if (bytesRead != Convert.FromBase64String(publicKeyBase64).Length)
            {
                // Public key có extra bytes phía sau — nghi ngờ tamper, từ chối
                return false;
            }

            return ecdsa.VerifyHash(hash, signature);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException or UnauthorizedAccessException)
        {
            // Fail-closed: mọi lỗi parse/đọc file/giải mã/từ chối quyền đều coi là không hợp lệ.
            // Caller (GitHubAppUpdateService) log chi tiết ở tầng cao hơn.
            return false;
        }
    }

    private static string ReadTrimmedBase64(string path) =>
        File.ReadAllText(path).Trim();
}
