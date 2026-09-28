using System.Security.Cryptography;

namespace Club.Server.Imaging;

/// <summary>
/// Файл цепочки загрузки. <c>status</c>: <c>ok</c>; <c>missing</c>; <c>invalid</c>; <c>unsigned</c>, <c>badSignature</c>,
/// <c>wrongSigner</c> — такой файл не загрузится на ПК с Secure Boot (без Secure Boot — загрузится).
/// </summary>
public sealed record BootFileCheck(
    string Name, string Location, bool Required, string Status, long? SizeBytes, string? Sha256, string? Component,
    IReadOnlyList<string> SignedBy, IReadOnlyList<string> Expected);

public sealed record BootChainReport(bool TftpChecked, IReadOnlyList<BootFileCheck> Files)
{
    /// <summary>Не хватает обязательных файлов — перезаливка не начнётся ни на одном ПК.</summary>
    public IReadOnlyList<string> Missing => Files.Where(f => f.Required && f.Status is "missing" or "invalid").Select(f => f.Name).ToList();

    /// <summary>Файлы, из-за которых не пройдёт Secure Boot (подписи нет, не та или файл испорчен).</summary>
    public IReadOnlyList<string> NotSecureBootReady =>
        Files.Where(f => f.Required && f.Expected.Count > 0 && f.Status != "ok").Select(f => f.Name).ToList();

    public BootFileCheck? File(string name) => Files.FirstOrDefault(f => f.Name == name);
}

/// <summary>
/// Проверка цепочки загрузки для перезаливки: прошивка → ipxe-shim.efi (подписан Microsoft) → ipxe.efi (подписан
/// iPXE) → wimboot (подписан Microsoft) → загрузчик Windows из boot.wim или boot/bootx64.efi (Windows UEFI CA 2023).
/// Подписи проверяются по-настоящему (<see cref="Authenticode"/>); доверие к корням решает прошивка ПК.
/// </summary>
public sealed class BootFiles(ImagingOptions options)
{
    public const string Shim = "ipxe-shim.efi";
    public const string Ipxe = "ipxe.efi";
    public const string Undi = "undionly.kpxe";
    public const string Wimboot = "wimboot";
    public const string BootManager2023 = "boot/bootx64.efi";

    private static readonly string[] Microsoft = [Authenticode.MicrosoftUefiCa2011, Authenticode.MicrosoftUefiCa2023];

    public BootChainReport Check()
    {
        var files = new List<BootFileCheck>();
        var tftp = !string.IsNullOrWhiteSpace(options.TftpRoot);
        if (tftp)
        {
            files.Add(Efi(options.TftpRoot, Shim, "tftp", required: true, Microsoft));
            files.Add(Efi(options.TftpRoot, Ipxe, "tftp", required: true, [Authenticode.IpxeCa]));
            files.Add(Plain(options.TftpRoot, Undi, "tftp", required: false));
        }

        files.Add(Efi(options.PxeRoot, Wimboot, "pxe", required: true, Microsoft));
        files.Add(Plain(options.PxeRoot, "boot/BCD", "pxe", required: true));
        files.Add(Plain(options.PxeRoot, "boot/boot.sdi", "pxe", required: true));
        files.Add(BootWim());
        files.Add(Efi(options.PxeRoot, BootManager2023, "pxe", required: false, [Authenticode.WindowsUefiCa2023]));
        return new BootChainReport(tftp, files);
    }

    private static BootFileCheck Efi(string root, string name, string location, bool required, string[] expected)
    {
        var path = Path.Combine(root, name);
        if (!System.IO.File.Exists(path))
        {
            return new(name, location, required, "missing", null, null, null, [], expected);
        }

        byte[] bytes;
        try
        {
            bytes = System.IO.File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(name, location, required, "missing", null, null, null, [], expected);
        }

        var info = Authenticode.Inspect(bytes);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var component = Authenticode.SbatComponent(info.Sbat);
        var signedBy = info.Signatures.Where(s => s.Valid).SelectMany(s => s.Authorities).Distinct().ToList();
        var status = !info.IsPe ? "invalid"
            : info.Signatures.Count == 0 ? "unsigned"
            : info.Signatures.All(s => !s.Valid) ? "badSignature"
            : expected.Any(signedBy.Contains) ? "ok"
            : "wrongSigner";
        return new(name, location, required, status, bytes.LongLength, sha, component, signedBy, expected);
    }

    private static BootFileCheck Plain(string root, string name, string location, bool required)
    {
        var path = Path.Combine(root, name);
        var exists = System.IO.File.Exists(path);
        return new(name, location, required, exists ? "ok" : "missing", exists ? new FileInfo(path).Length : null, null, null, [], []);
    }

    private BootFileCheck BootWim()
    {
        const string name = "sources/boot.wim";
        var path = Path.Combine(options.PxeRoot, name);
        if (!System.IO.File.Exists(path))
        {
            return new(name, "pxe", true, "missing", null, null, null, [], []);
        }

        string? component;
        try
        {
            var image = WimFile.ReadImages(path).FirstOrDefault();
            component = image is null ? null : $"{image.Name} {image.Build}".Trim();
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or EndOfStreamException or IOException)
        {
            return new(name, "pxe", true, "invalid", new FileInfo(path).Length, null, null, [], []);
        }

        return new(name, "pxe", true, "ok", new FileInfo(path).Length, null, component, [], []);
    }
}
