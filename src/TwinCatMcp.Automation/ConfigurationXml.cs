using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using TwinCatMcp.Automation.Models;

namespace TwinCatMcp.Automation;

/// <summary>XML patches for project configuration only; never accept executable XML Methods from callers.</summary>
internal static class ConfigurationXml
{
    internal static XDocument Parse(string xml, string root)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024,
        });
        var document = XDocument.Load(reader);
        if (document.Root?.Name != root)
            throw new ArgumentException($"Expected XML root '{root}'.");
        return document;
    }

    internal static string Tree(string section, params object[] content) =>
        new XElement("TreeItem", new XElement(section, content)).ToString(SaveOptions.DisableFormatting);

    internal static string Adapter(string description, string guid, string mac)
    {
        var id = guid.StartsWith(@"\DEVICE\", StringComparison.OrdinalIgnoreCase) ? guid[8..] : guid;
        if (!Guid.TryParse(id, out var parsed)) throw new ArgumentException("deviceGuid must be a network adapter GUID.");
        if (mac.Length != 12 || !mac.All(Uri.IsHexDigit)) throw new ArgumentException("mac must contain exactly 12 hexadecimal digits.");
        return Tree("DeviceDef", new XElement("AddressInfo", new XElement("Pnp",
            new XElement("DeviceDesc", description), new XElement("DeviceName", @"\DEVICE\" + parsed.ToString("B")),
            new XElement("DeviceData", mac.ToLowerInvariant()))));
    }

    internal static string TaskSettings(string xml, int? cycleTimeUs, int? priority, int? coreId)
    {
        if (cycleTimeUs is <= 0) throw new ArgumentOutOfRangeException(nameof(cycleTimeUs));
        if (priority is < 1 or > 255) throw new ArgumentOutOfRangeException(nameof(priority));
        if (coreId is < 0 or > 63) throw new ArgumentOutOfRangeException(nameof(coreId));
        var document = Parse(xml, "TreeItem");
        var definition = document.Root!.Element("TaskDef") ?? throw new ArgumentException("Tree item has no TaskDef.");
        if (cycleTimeUs is { } cycle)
        {
            definition.SetElementValue("CycleTime", cycle * 10L);
            foreach (var context in document.Descendants("Context"))
                if (context.Element("CycleTime") is { } value) value.Value = (cycle * 1000L).ToString(CultureInfo.InvariantCulture);
        }
        if (priority is { } p) definition.SetElementValue("Priority", p);
        if (coreId is { } c) definition.SetElementValue("CpuAffinity", $"#x{1UL << c:x16}");
        // Return only the settings we change: round-tripping all ProduceXml content can replay Methods.
        var patch = new XElement("TreeItem", new XElement("TaskDef",
            definition.Elements().Where(e => (cycleTimeUs.HasValue && e.Name == "CycleTime") ||
                (priority.HasValue && e.Name == "Priority") || (coreId.HasValue && e.Name == "CpuAffinity")).Select(e => new XElement(e))));
        if (cycleTimeUs.HasValue && document.Root.Element("TcModuleInstance") is { } module)
        {
            var contexts = module.Element("Module")?.Element("Contexts");
            if (contexts is not null)
                patch.Add(new XElement("TcModuleInstance", new XElement("Module", new XElement("Contexts",
                    contexts.Elements("Context").Where(c => c.Element("CycleTime") is not null).Select(c =>
                        new XElement("Context", c.Attributes().Select(a => new XAttribute(a)),
                            c.Elements().Where(e => e.Name.LocalName is "Id" or "ID" or "Name").Select(e => new XElement(e)),
                            new XElement(c.Element("CycleTime")!)))))));
        }
        return patch.ToString(SaveOptions.DisableFormatting);
    }

    internal static string RealtimeCores(int total, int isolated, CpuCoreConfiguration[] cores)
    {
        if (total is < 1 or > 64 || isolated < 0 || isolated > total)
            throw new ArgumentOutOfRangeException(nameof(total), "Require 1..64 total cores and 0..total isolated cores.");
        if (cores.Any(c => c.CoreId < 0 || c.CoreId >= total || c.BaseTimeUs <= 0) ||
            cores.Select(c => c.CoreId).Distinct().Count() != cores.Length)
            throw new ArgumentException("Core IDs must be unique and within total cores; base times must be positive.");
        return Tree("RTimeSetDef", new XElement("MaxCPUs", new XAttribute("NonWindowsCPUs", isolated), total),
            new XElement("CPUs", cores.Select(c => new XElement("CPU", new XAttribute("id", c.CoreId), new XElement("BaseTime", c.BaseTimeUs * 10L)))));
    }
}
