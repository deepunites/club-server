using Club.Server.Imaging;

namespace Club.Server.Tests;

/// <summary>
/// Подписи EFI-файлов загрузки. Настоящие файлы — если заданы <c>IPXE_BUNDLE</c> (распакованный ipxeboot.tar.gz
/// официального релиза iPXE) и <c>WIMBOOT</c>; синтетические PE, подписанные тестовым CA, — всегда.
/// </summary>
public sealed class AuthenticodeTests
{
    [Fact]
    public void Real_boot_chain_signatures_are_recognized()
    {
        if (Environment.GetEnvironmentVariable("IPXE_BUNDLE") is not { Length: > 0 } bundle || Environment.GetEnvironmentVariable("WIMBOOT") is not { Length: > 0 } wimboot)
        {
            return;
        }

        var shim = Authenticode.Inspect(Path.Combine(bundle, "x86_64-sb", "shimx64.efi"));
        Assert.True(shim.SignedBy(Authenticode.MicrosoftUefiCa2011), string.Join("; ", shim.Signatures));
        Assert.False(shim.SignedBy(Authenticode.MicrosoftUefiCa2023));
        Assert.Contains("shim.ipxe", shim.Sbat);

        var ipxe = Authenticode.Inspect(Path.Combine(bundle, "x86_64-sb", "ipxe.efi"));
        Assert.True(ipxe.SignedBy(Authenticode.IpxeCa), string.Join("; ", ipxe.Signatures));
        Assert.Equal("ipxe.efi 2.0.0 (g12798)", Authenticode.SbatComponent(ipxe.Sbat));

        var unsigned = Authenticode.Inspect(Path.Combine(bundle, "x86_64", "ipxe.efi"));
        Assert.True(unsigned.IsPe);
        Assert.Empty(unsigned.Signatures);

        var wim = Authenticode.Inspect(wimboot);
        Assert.True(wim.SignedBy(Authenticode.MicrosoftUefiCa2011), string.Join("; ", wim.Signatures));
        Assert.True(wim.SignedBy(Authenticode.MicrosoftUefiCa2023)); // двойная подпись
        Assert.Equal("wimboot v2.9.0", Authenticode.SbatComponent(wim.Sbat));

        // Один изменённый байт — подпись больше не сходится.
        var tampered = File.ReadAllBytes(wimboot);
        tampered[0x400] ^= 0xFF;
        Assert.All(Authenticode.Inspect(tampered).Signatures, s => Assert.False(s.Valid));

        Assert.False(Authenticode.Inspect(Path.Combine(bundle, "x86_64", "undionly.kpxe")).IsPe);
    }
}

public sealed class AuthenticodeSyntheticTests
{
    [Fact]
    public void Signed_file_is_valid_and_reports_its_authority()
    {
        var file = Club.TestSupport.TestEfi.Signed(Authenticode.MicrosoftUefiCa2023);
        var info = Authenticode.Inspect(file);
        var signature = Assert.Single(info.Signatures);
        Assert.True(signature.Valid, signature.Error);
        Assert.Equal("Test Signer", signature.Signer);
        Assert.Equal([Authenticode.MicrosoftUefiCa2023], signature.Authorities);
        Assert.True(info.SignedBy(Authenticode.MicrosoftUefiCa2023));
        Assert.False(info.SignedBy(Authenticode.MicrosoftUefiCa2011));
        Assert.Equal("wimboot v9.9.9", Authenticode.SbatComponent(info.Sbat));
    }

    [Fact]
    public void Modified_or_unsigned_files_are_not_trusted()
    {
        var file = Club.TestSupport.TestEfi.Signed(Authenticode.IpxeCa);
        file[0x210] ^= 0x01; // байт в секции .sbat
        var info = Authenticode.Inspect(file);
        Assert.False(info.SignedBy(Authenticode.IpxeCa));
        Assert.Contains("does not match", Assert.Single(info.Signatures).Error);

        var unsigned = Authenticode.Inspect(Club.TestSupport.TestEfi.Pe());
        Assert.True(unsigned.IsPe);
        Assert.Empty(unsigned.Signatures);

        Assert.False(Authenticode.Inspect(new byte[100]).IsPe);
        Assert.False(Authenticode.Inspect("hello, not a PE at all"u8.ToArray()).IsPe);
    }
}
