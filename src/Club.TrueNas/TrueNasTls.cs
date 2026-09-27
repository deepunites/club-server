using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Club.TrueNas;

/// <summary>Проверка сертификата TrueNAS по внутреннему CA клуба (цепочка + имя хоста), без системного хранилища.</summary>
public static class TrueNasTls
{
    public static RemoteCertificateValidationCallback Validator(string caCertificatePath)
    {
        if (string.IsNullOrWhiteSpace(caCertificatePath) || !File.Exists(caCertificatePath))
        {
            throw new TrueNasUnavailableException($"CA certificate for TrueNAS not found: '{caCertificatePath}'");
        }

        var roots = new X509Certificate2Collection();
        roots.ImportFromPemFile(caCertificatePath);
        return (_, certificate, _, errors) =>
        {
            // Имя хоста проверяет платформа; неизвестный корень ожидаем и проверяем сами по CA клуба.
            if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
            {
                return false;
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(roots);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            using var leaf = new X509Certificate2(certificate);
            return chain.Build(leaf);
        };
    }
}
