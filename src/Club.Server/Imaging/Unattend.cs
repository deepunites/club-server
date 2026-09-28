using System.Xml;
using System.Xml.Linq;

namespace Club.Server.Imaging;

/// <summary>
/// Личность машины при развёртывании: имя компьютера в проходе specialize. Sysprep кладёт свой unattend в
/// <c>%WINDIR%\Panther\unattend.xml</c> — его не заменяем, а дописываем (OOBE-настройки золотого образа сохраняются).
/// </summary>
public static class Unattend
{
    public static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";
    private const string ShellSetup = "Microsoft-Windows-Shell-Setup";

    /// <summary>NetBIOS-имя: до 15 символов, латиница, цифры, дефис, не только цифры.</summary>
    public static string ComputerName(string name, int seat)
    {
        var chars = name.ToUpperInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var result = new string(chars).Trim('-');
        while (result.Contains("--", StringComparison.Ordinal))
        {
            result = result.Replace("--", "-", StringComparison.Ordinal);
        }

        if (result.Length > 15)
        {
            result = result[..15].TrimEnd('-');
        }

        return result.Length == 0 || result.All(char.IsAsciiDigit) ? $"PC-{seat:D2}" : result;
    }

    public static string Merge(string? existing, string computerName)
    {
        var doc = string.IsNullOrWhiteSpace(existing)
            ? new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement(Ns + "unattend"))
            : XDocument.Parse(existing, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new XmlException("empty unattend");
        if (root.Name != Ns + "unattend")
        {
            throw new XmlException("not an unattend file");
        }

        var specialize = root.Elements(Ns + "settings").FirstOrDefault(s => (string?)s.Attribute("pass") == "specialize");
        if (specialize is null)
        {
            specialize = new XElement(Ns + "settings", new XAttribute("pass", "specialize"));
            root.AddFirst(specialize);
        }

        var shell = specialize.Elements(Ns + "component").FirstOrDefault(c =>
            (string?)c.Attribute("name") == ShellSetup && (string?)c.Attribute("processorArchitecture") == "amd64");
        if (shell is null)
        {
            shell = new XElement(Ns + "component",
                new XAttribute("name", ShellSetup),
                new XAttribute("processorArchitecture", "amd64"),
                new XAttribute("publicKeyToken", "31bf3856ad364e35"),
                new XAttribute("language", "neutral"),
                new XAttribute("versionScope", "nonSxS"));
            specialize.Add(shell);
        }

        var element = shell.Element(Ns + "ComputerName");
        if (element is null)
        {
            shell.AddFirst(new XElement(Ns + "ComputerName", computerName));
        }
        else
        {
            element.Value = computerName;
        }

        using var writer = new Utf8StringWriter();
        doc.Save(writer);
        return writer.ToString();
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override System.Text.Encoding Encoding => new System.Text.UTF8Encoding(false);
    }
}
