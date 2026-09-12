using System.Xml.Linq;

namespace DshLauncher.UnitTests;

internal static class XamlTestResources
{
    internal static string SourceFile(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "DshLauncher", name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(name);
    }

    internal static void NormalizeAssemblyNamespaces(XElement root)
    {
        // Standalone XamlReader has no local application assembly, unlike
        // compiled BAML. Preserve production markup, only qualify its CLR URIs.
        string Qualify(string uri) => uri.StartsWith("clr-namespace:DshLauncher", StringComparison.Ordinal)
            && !uri.Contains(";assembly=", StringComparison.Ordinal) ? uri + ";assembly=DSH Launcher" : uri;
        foreach (var element in root.DescendantsAndSelf())
        {
            element.Name = XName.Get(element.Name.LocalName, Qualify(element.Name.NamespaceName));
            foreach (var attribute in element.Attributes().ToArray())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    attribute.Value = Qualify(attribute.Value);
                    continue;
                }
                var namespaceName = Qualify(attribute.Name.NamespaceName);
                if (namespaceName == attribute.Name.NamespaceName) continue;
                element.SetAttributeValue(XName.Get(attribute.Name.LocalName, namespaceName), attribute.Value);
                attribute.Remove();
            }
        }
        root.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:DshLauncher;assembly=DSH Launcher");
        root.SetAttributeValue(XNamespace.Xmlns + "sys", "clr-namespace:System;assembly=mscorlib");
    }
}
