using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace Club.Server.Imaging;

/// <summary>
/// Подпись Authenticode одного файла. <c>Valid</c> — подпись математически верна и хэш файла совпал с подписанным
/// (файл не испорчен и не подменён). Доверие к корню решает прошивка ПК; здесь — только кто подписал.
/// </summary>
public sealed record PeSignature(bool Valid, string Signer, IReadOnlyList<string> Chain, string? Error)
{
    /// <summary>Известные центры сертификации Secure Boot, встреченные в цепочке.</summary>
    public IReadOnlyList<string> Authorities => Authenticode.KnownAuthorities.Where(a => Chain.Contains(a)).ToList();
}

/// <summary>EFI-файл: PE или нет, строка SBAT (компонент и версия), подписи (включая вложенные — двойная подпись).</summary>
public sealed record PeFile(bool IsPe, string? Sbat, IReadOnlyList<PeSignature> Signatures)
{
    public bool SignedBy(string authority) => Signatures.Any(s => s.Valid && s.Authorities.Contains(authority));
}

/// <summary>
/// Проверка подписи EFI-файлов загрузки (shim, iPXE, wimboot, загрузчик Windows) без Windows: разбор PE, хэш
/// Authenticode, PKCS#7 SignedData и вложенные подписи (OID 1.3.6.1.4.1.311.2.4.1). Проверено на ipxe-shim.efi,
/// ipxe.efi из официального релиза iPXE 2.0.0 и wimboot 2.9.0 (AuthenticodeTests).
/// </summary>
public static class Authenticode
{
    public const string MicrosoftUefiCa2011 = "Microsoft Corporation UEFI CA 2011";
    public const string MicrosoftUefiCa2023 = "Microsoft UEFI CA 2023";
    public const string WindowsPca2011 = "Microsoft Windows Production PCA 2011";
    public const string WindowsUefiCa2023 = "Windows UEFI CA 2023";
    public const string IpxeCa = "iPXE Secure Boot CA";

    public static readonly string[] KnownAuthorities = [MicrosoftUefiCa2011, MicrosoftUefiCa2023, WindowsPca2011, WindowsUefiCa2023, IpxeCa];

    private const string SpcIndirectDataOid = "1.3.6.1.4.1.311.2.1.4";
    private const string NestedSignatureOid = "1.3.6.1.4.1.311.2.4.1";

    public static PeFile Inspect(string path) => Inspect(File.ReadAllBytes(path));

    public static PeFile Inspect(byte[] file)
    {
        if (!TryParse(file, out var pe))
        {
            return new PeFile(false, null, []);
        }

        var signatures = new List<PeSignature>();
        var position = pe.CertOffset;
        var end = pe.CertOffset + pe.CertSize;
        while (pe.CertSize > 0 && position + 8 <= end)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(position));
            var type = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(position + 6));
            if (length < 8 || position + length > end)
            {
                signatures.Add(new PeSignature(false, "", [], "broken certificate table"));
                break;
            }

            if (type == 0x0002)
            {
                Collect(file.AsSpan(position + 8, length - 8).ToArray(), pe, file, signatures, depth: 0);
            }

            position += (length + 7) & ~7;
        }

        return new PeFile(true, pe.Sbat, signatures);
    }

    private static void Collect(byte[] pkcs7, Pe pe, byte[] file, List<PeSignature> signatures, int depth)
    {
        var cms = new SignedCms();
        try
        {
            cms.Decode(pkcs7);
        }
        catch (CryptographicException ex)
        {
            signatures.Add(new PeSignature(false, "", [], $"not PKCS#7: {ex.Message}"));
            return;
        }

        signatures.Add(Verify(cms, pe, file));
        if (depth > 2)
        {
            return;
        }

        foreach (var signer in cms.SignerInfos)
        {
            foreach (var attribute in signer.UnsignedAttributes)
            {
                if (attribute.Oid?.Value != NestedSignatureOid)
                {
                    continue;
                }

                foreach (var value in attribute.Values)
                {
                    Collect(value.RawData, pe, file, signatures, depth + 1);
                }
            }
        }
    }

    private static PeSignature Verify(SignedCms cms, Pe pe, byte[] file)
    {
        var signer = cms.SignerInfos.Count > 0 ? cms.SignerInfos[0] : null;
        var chain = Chain(signer?.Certificate, cms.Certificates);
        var name = signer?.Certificate?.GetNameInfo(X509NameType.SimpleName, false) ?? "";
        if (cms.ContentInfo.ContentType.Value != SpcIndirectDataOid || signer is null)
        {
            return new PeSignature(false, name, chain, "not an Authenticode signature");
        }

        try
        {
            signer.CheckSignature(verifySignatureOnly: true);
        }
        catch (CryptographicException ex)
        {
            return new PeSignature(false, name, chain, $"signature: {ex.Message}");
        }

        var (algorithm, expected) = IndirectDigest(cms.ContentInfo.Content);
        using var hash = algorithm switch
        {
            "2.16.840.1.101.3.4.2.1" => IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            "2.16.840.1.101.3.4.2.2" => IncrementalHash.CreateHash(HashAlgorithmName.SHA384),
            "2.16.840.1.101.3.4.2.3" => IncrementalHash.CreateHash(HashAlgorithmName.SHA512),
            "1.3.14.3.2.26" => IncrementalHash.CreateHash(HashAlgorithmName.SHA1),
            _ => null,
        };
        if (hash is null)
        {
            return new PeSignature(false, name, chain, $"digest algorithm {algorithm}");
        }

        return PeHash(file, pe, hash).SequenceEqual(expected)
            ? new PeSignature(true, name, chain, null)
            : new PeSignature(false, name, chain, "file does not match its signature (modified or corrupted)");
    }

    /// <summary>Хэш Authenticode: весь файл до таблицы сертификатов, кроме поля CheckSum и записи Security Directory.</summary>
    public static byte[] PeHash(byte[] file, Pe pe, IncrementalHash hash)
    {
        var end = pe.CertSize > 0 ? pe.CertOffset : file.Length;
        hash.AppendData(file, 0, pe.ChecksumOffset);
        hash.AppendData(file, pe.ChecksumOffset + 4, pe.SecurityDirOffset - pe.ChecksumOffset - 4);
        hash.AppendData(file, pe.SecurityDirOffset + 8, end - pe.SecurityDirOffset - 8);
        return hash.GetHashAndReset();
    }

    /// <summary>SpcIndirectDataContent ::= SEQUENCE { data, messageDigest DigestInfo }.</summary>
    private static (string Algorithm, byte[] Digest) IndirectDigest(byte[] content)
    {
        var reader = new AsnReader(content, AsnEncodingRules.BER);
        if (reader.PeekTag().HasSameClassAndValue(Asn1Tag.PrimitiveOctetString))
        {
            reader = new AsnReader(reader.ReadOctetString(), AsnEncodingRules.BER);
        }

        var indirect = reader.ReadSequence();
        indirect.ReadSequence(); // SpcAttributeTypeAndOptionalValue
        var digestInfo = indirect.ReadSequence();
        var algorithm = digestInfo.ReadSequence().ReadObjectIdentifier();
        return (algorithm, digestInfo.ReadOctetString());
    }

    private static List<string> Chain(X509Certificate2? leaf, X509Certificate2Collection bag)
    {
        var names = new List<string>();
        var current = leaf;
        for (var i = 0; current is not null && i < 8; i++)
        {
            names.Add(current.GetNameInfo(X509NameType.SimpleName, false));
            var issuer = current.GetNameInfo(X509NameType.SimpleName, true);
            var next = bag.Cast<X509Certificate2>().FirstOrDefault(c => c.SubjectName.RawData.AsSpan().SequenceEqual(current.IssuerName.RawData) && !ReferenceEquals(c, current) && c.Thumbprint != current.Thumbprint);
            if (next is null)
            {
                names.Add(issuer);
            }

            current = next;
        }

        return names.Distinct().ToList();
    }

    public readonly record struct Pe(int ChecksumOffset, int SecurityDirOffset, int CertOffset, int CertSize, string? Sbat);

    public static bool TryParse(byte[] file, out Pe pe)
    {
        pe = default;
        if (file.Length < 0x40 || file[0] != 'M' || file[1] != 'Z')
        {
            return false;
        }

        var peOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x3C));
        if (peOffset <= 0 || peOffset + 24 > file.Length || !file.AsSpan(peOffset, 4).SequenceEqual("PE\0\0"u8))
        {
            return false;
        }

        var coff = peOffset + 4;
        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(coff + 2));
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(coff + 16));
        var optional = coff + 20;
        if (optional + optionalSize > file.Length)
        {
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(optional));
        var dataDirectories = optional + (magic == 0x20B ? 112 : 96);
        var securityDir = dataDirectories + 4 * 8;
        if (securityDir + 8 > optional + optionalSize)
        {
            return false;
        }

        var certOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(securityDir));
        var certSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(securityDir + 4));
        if (certSize < 0 || certOffset < 0 || (certSize > 0 && (certOffset < securityDir || (long)certOffset + certSize > file.Length)))
        {
            return false;
        }

        string? sbat = null;
        var sections = optional + optionalSize;
        for (var i = 0; i < sectionCount && sections + 40 * (i + 1) <= file.Length; i++)
        {
            var header = file.AsSpan(sections + 40 * i, 40);
            if (!header[..8].TrimEnd((byte)0).SequenceEqual(".sbat"u8))
            {
                continue;
            }

            var rawSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            var rawPointer = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            if (rawPointer > 0 && rawSize > 0 && (long)rawPointer + rawSize <= file.Length)
            {
                sbat = System.Text.Encoding.UTF8.GetString(file, rawPointer, rawSize).TrimEnd('\0').Trim();
            }
        }

        pe = new Pe(optional + 64, securityDir, certOffset, certSize, sbat);
        return true;
    }

    /// <summary>Компонент из SBAT (не строки sbat/shim базового уровня): «wimboot v2.9.0», «ipxe.efi 2.0.0 (g12798)».</summary>
    public static string? SbatComponent(string? sbat) =>
        sbat?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(','))
            .Where(f => f.Length >= 5 && f[0] != "sbat")
            .Select(f => $"{f[3]} {f[4]}".Trim())
            .LastOrDefault();
}
