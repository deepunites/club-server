using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Club.Server.Imaging;

namespace Club.TestSupport;

/// <summary>
/// Синтетические EFI-файлы для тестов: минимальный PE32+ с секцией .sbat и подпись Authenticode тестовым CA
/// (имена CA задаются, чтобы изображать цепочки Microsoft/iPXE). Хэш — тот же алгоритм, что проверен на настоящих файлах.
/// </summary>
public static class TestEfi
{
    public static byte[] Pe(string sbat = "sbat,1,SBAT Version,sbat,1,x\nwimboot,1,iPXE,wimboot,v9.9.9,x")
    {
        var file = new byte[0x400];
        file[0] = (byte)'M';
        file[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x3C), 0x40);
        "PE\0\0"u8.CopyTo(file.AsSpan(0x40));
        var coff = 0x44;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(coff), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(coff + 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(coff + 16), 240);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(coff + 18), 0x22);
        var optional = coff + 20;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(optional), 0x20B);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(optional + 60), 0x200); // SizeOfHeaders
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(optional + 108), 16); // NumberOfRvaAndSizes
        var section = optional + 240;
        ".sbat"u8.CopyTo(file.AsSpan(section));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(section + 16), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(section + 20), 0x200);
        System.Text.Encoding.ASCII.GetBytes(sbat + "\n").CopyTo(file.AsSpan(0x200));
        return file;
    }

    /// <summary>Цепочка «корень → промежуточный с именем <paramref name="authority"/> → подписант».</summary>
    public static (X509Certificate2 Signer, X509Certificate2[] Chain) Ca(string authority, string signer = "Test Signer")
    {
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest($"CN=Test Root for {authority}", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest($"CN={authority}", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var ca = caRequest.Create(root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMonths(6), [1, 2, 3]).CopyWithPrivateKey(caKey);

        var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest($"CN={signer}", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var leaf = leafRequest.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMonths(1), [4, 5, 6]).CopyWithPrivateKey(leafKey);
        return (leaf, [ca, root]);
    }

    public static byte[] Sign(byte[] pe, X509Certificate2 signer, X509Certificate2[] chain)
    {
        if (!Authenticode.TryParse(pe, out var parsed))
        {
            throw new ArgumentException("not a PE");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var digest = Authenticode.PeHash(pe, parsed, hash);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier("1.3.6.1.4.1.311.2.1.15"); // SPC_PE_IMAGE_DATAOBJ
                using (writer.PushSequence())
                {
                    writer.WriteBitString([]);
                }
            }

            using (writer.PushSequence())
            {
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1");
                    writer.WriteNull();
                }

                writer.WriteOctetString(digest);
            }
        }

        var cms = new SignedCms(new ContentInfo(new Oid("1.3.6.1.4.1.311.2.1.4"), writer.Encode()), detached: false);
        var cmsSigner = new CmsSigner(signer) { IncludeOption = X509IncludeOption.EndCertOnly };
        cmsSigner.Certificates.AddRange(chain);
        cms.ComputeSignature(cmsSigner, silent: true);
        var der = cms.Encode();

        var certOffset = (pe.Length + 7) & ~7;
        var entryLength = 8 + der.Length;
        var tableSize = (entryLength + 7) & ~7;
        var signed = new byte[certOffset + tableSize];
        pe.CopyTo(signed, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(certOffset), (uint)entryLength);
        BinaryPrimitives.WriteUInt16LittleEndian(signed.AsSpan(certOffset + 4), 0x0200);
        BinaryPrimitives.WriteUInt16LittleEndian(signed.AsSpan(certOffset + 6), 0x0002);
        der.CopyTo(signed, certOffset + 8);
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(parsed.SecurityDirOffset), (uint)certOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(parsed.SecurityDirOffset + 4), (uint)tableSize);
        return signed;
    }

    /// <summary>
    /// Минимальный набор файлов WinPE в <paramref name="pxeRoot"/> (BCD, boot.sdi, boot.wim, wimboot с подписью под
    /// Microsoft UEFI CA 2011) — чтобы перезаливку можно было запросить.
    /// </summary>
    public static void PxeRoot(string pxeRoot, string wimFixture)
    {
        Directory.CreateDirectory(Path.Combine(pxeRoot, "boot"));
        Directory.CreateDirectory(Path.Combine(pxeRoot, "sources"));
        File.WriteAllBytes(Path.Combine(pxeRoot, "wimboot"), Signed(Authenticode.MicrosoftUefiCa2011));
        File.WriteAllText(Path.Combine(pxeRoot, "boot", "BCD"), "bcd");
        File.WriteAllText(Path.Combine(pxeRoot, "boot", "boot.sdi"), "sdi");
        File.Copy(wimFixture, Path.Combine(pxeRoot, "sources", "boot.wim"), overwrite: true);
    }

    /// <summary>PE, подписанный тестовой цепочкой с именем CA <paramref name="authority"/>.</summary>
    public static byte[] Signed(string authority, string? sbat = null)
    {
        var (signer, chain) = Ca(authority);
        return Sign(sbat is null ? Pe() : Pe(sbat), signer, chain);
    }
}
