using System.Reflection;
using System.Xml;
using System.Xml.Linq;

namespace PdfPro.App.Tests.Views;

/// <summary>
/// Reads the App's XAML the way WPF does when a view is loaded, for the faults the markup compiler lets through and
/// that only show as a crash when that view opens: a value that is not a member of its enum (a misspelt WPF-UI symbol,
/// <c>Visibility="Hiden"</c>), a <c>{x:Static}</c> member or an <c>{x:Type}</c> that does not exist, a setter or
/// trigger naming a property its target does not have, or an enum value no property of a markup extension
/// (<c>{Binding Mode=…}</c>, <c>{RelativeSource …}</c>) accepts.
/// </summary>
/// <remarks>
/// Only reflection that a <c>MetadataLoadContext</c> also offers is used, so the same code can be tried outside
/// Windows against the WPF assemblies.
/// </remarks>
internal sealed class XamlValueChecker
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string Compatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private const string Blend = "http://schemas.microsoft.com/expression/blend/2008";

    private readonly Assembly _app;
    private readonly Func<string, Assembly?> _load;
    private readonly Dictionary<string, List<(Assembly Assembly, string Namespace)>> _xmlns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Type?> _types = new(StringComparer.Ordinal);

    /// <param name="app">The App's assembly: <c>clr-namespace:</c> without an assembly means this one.</param>
    /// <param name="load">Loads an assembly by its simple name, or null.</param>
    public XamlValueChecker(Assembly app, Func<string, Assembly?> load)
    {
        _app = app;
        _load = load;
        var assemblies = new List<Assembly> { app };
        foreach (var reference in app.GetReferencedAssemblies())
        {
            if (reference.Name is { } name && Load(name) is { } assembly)
            {
                assemblies.Add(assembly);
            }
        }

        foreach (var assembly in assemblies)
        {
            foreach (var attribute in assembly.GetCustomAttributesData())
            {
                if (attribute.AttributeType.FullName != "System.Windows.Markup.XmlnsDefinitionAttribute"
                    || attribute.ConstructorArguments.Count != 2
                    || attribute.ConstructorArguments[0].Value is not string uri
                    || attribute.ConstructorArguments[1].Value is not string clrNamespace)
                {
                    continue;
                }

                var target = assembly;
                foreach (var named in attribute.NamedArguments)
                {
                    if (named.MemberName == "AssemblyName" && named.TypedValue.Value is string other)
                    {
                        target = Load(new AssemblyName(other).Name ?? other) ?? assembly;
                    }
                }

                if (!_xmlns.TryGetValue(uri, out var list))
                {
                    _xmlns[uri] = list = [];
                }

                list.Add((target, clrNamespace));
            }
        }
    }

    /// <summary>Every fault of one XAML file, as "file(line): what".</summary>
    public IReadOnlyList<string> Check(string path)
        => Check(XDocument.Load(path, LoadOptions.SetLineInfo), Path.GetFileName(path));

    /// <summary>Every fault of a XAML document, as "file(line): what".</summary>
    public IReadOnlyList<string> Check(XDocument document, string file)
    {
        var problems = new List<string>();
        foreach (var element in document.Root!.DescendantsAndSelf())
        {
            CheckElement(element, problems, file);
        }

        return problems;
    }

    private void CheckElement(XElement element, List<string> problems, string file)
    {
        var ns = element.Name.NamespaceName;
        if (ns is XamlNamespace or Compatibility or Blend || IsIgnored(element, element.Name.Namespace))
        {
            return;
        }

        void Report(XObject at, string what)
            => problems.Add($"{file}({(at as IXmlLineInfo)?.LineNumber}): {what}");

        var name = element.Name.LocalName;
        Type? type = null;
        if (!name.Contains('.', StringComparison.Ordinal))
        {
            type = ResolveType(element.Name.Namespace, name) ?? ResolveType(element.Name.Namespace, name + "Extension");
            if (type is null)
            {
                Report(element, $"<{name}> is not a type this XAML can find");
                return;
            }
        }

        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration
                || attribute.Name.NamespaceName is XamlNamespace or Compatibility or Blend
                || IsIgnored(element, attribute.Name.Namespace))
            {
                continue;
            }

            var value = attribute.Value;
            var attributeName = attribute.Name.LocalName;
            CheckMarkup(element, attribute, value, problems, file);

            Type? propertyType;
            if (attributeName.Contains('.', StringComparison.Ordinal))
            {
                var ownerNamespace = attribute.Name.Namespace == XNamespace.None
                    ? element.GetDefaultNamespace()
                    : attribute.Name.Namespace;
                var dot = attributeName.LastIndexOf('.');
                var owner = ResolveType(ownerNamespace, attributeName[..dot]);
                if (owner is null)
                {
                    Report(attribute, $"{attributeName}: {attributeName[..dot]} is not a type this XAML can find");
                    continue;
                }

                propertyType = AttachedPropertyType(owner, attributeName[(dot + 1)..]) ?? PropertyType(owner, attributeName[(dot + 1)..]);
                if (propertyType is null && Event(owner, attributeName[(dot + 1)..]) is null)
                {
                    Report(attribute, $"{attributeName}: {owner.Name} has no such attached property");
                    continue;
                }
            }
            else if (type is not null)
            {
                propertyType = PropertyType(type, attributeName);
                if (propertyType is null && Event(type, attributeName) is null
                    && !(name is "Setter" or "Trigger" or "Condition" && attributeName is "Property" or "Value" or "TargetName"))
                {
                    Report(attribute, $"{name}.{attributeName}: no such property");
                    continue;
                }
            }
            else
            {
                continue;
            }

            if (propertyType is not null && !IsMarkup(value) && Problem(propertyType, value) is { } problem)
            {
                Report(attribute, $"{name}.{attributeName}=\"{value}\": {problem}");
            }
        }

        if (name is "Setter" or "Trigger" or "Condition")
        {
            CheckSetter(element, problems, file);
        }
    }

    /// <summary>A Setter's (Trigger's, Condition's) Property must exist on its target and its Value must fit it.</summary>
    private void CheckSetter(XElement element, List<string> problems, string file)
    {
        var property = (string?)element.Attribute("Property");
        if (property is null || IsMarkup(property))
        {
            return;
        }

        var line = (element as IXmlLineInfo).LineNumber;
        Type? target;
        var propertyName = property;
        var dot = property.LastIndexOf('.');
        if (dot > 0)
        {
            target = ResolveTypeName(element, property[..dot]);
            propertyName = property[(dot + 1)..];
            if (target is null)
            {
                problems.Add($"{file}({line}): {element.Name.LocalName} Property=\"{property}\": the type is not found");
                return;
            }
        }
        else
        {
            target = SetterTarget(element);
            if (target is null)
            {
                return;
            }
        }

        var type = PropertyType(target, propertyName) ?? AttachedPropertyType(target, propertyName);
        if (type is null)
        {
            problems.Add($"{file}({line}): {element.Name.LocalName} Property=\"{property}\": {target.Name} has no such property");
            return;
        }

        var value = (string?)element.Attribute("Value");
        if (value is not null && !IsMarkup(value) && Problem(type, value) is { } problem)
        {
            problems.Add($"{file}({line}): {element.Name.LocalName} {property}=\"{value}\": {problem}");
        }
    }

    /// <summary>What a setter without an owner in its Property sets: the TargetName element, or the style's or template's TargetType.</summary>
    private Type? SetterTarget(XElement element)
    {
        var targetName = (string?)element.Attribute("TargetName") ?? (string?)element.Attribute("SourceName");
        foreach (var ancestor in element.Ancestors())
        {
            var local = ancestor.Name.LocalName;
            if (local is not ("Style" or "ControlTemplate" or "DataTemplate" or "HierarchicalDataTemplate"))
            {
                continue;
            }

            if (targetName is not null)
            {
                var named = ancestor.Descendants().FirstOrDefault(
                    e => (string?)e.Attribute(XName.Get("Name", XamlNamespace)) == targetName || (string?)e.Attribute("Name") == targetName);
                return named is null ? null : ResolveType(named.Name.Namespace, named.Name.LocalName);
            }

            var targetType = (string?)ancestor.Attribute("TargetType");
            return targetType is null ? null : ResolveTypeName(ancestor, targetType);
        }

        return null;
    }

    /// <summary>The <c>{…}</c> of an attribute: x:Static members and x:Type types must exist; enum arguments must fit.</summary>
    private void CheckMarkup(XElement element, XAttribute attribute, string value, List<string> problems, string file)
    {
        if (!IsMarkup(value))
        {
            return;
        }

        var line = (attribute as IXmlLineInfo).LineNumber;
        foreach (var (extension, positional, named) in MarkupExtensions(value))
        {
            var colon = extension.IndexOf(':', StringComparison.Ordinal);
            var prefix = colon < 0 ? string.Empty : extension[..colon];
            var local = colon < 0 ? extension : extension[(colon + 1)..];
            var ns = prefix.Length == 0 ? element.GetDefaultNamespace() : element.GetNamespaceOfPrefix(prefix);
            if (ns is null)
            {
                problems.Add($"{file}({line}): {{{extension}}}: the prefix \"{prefix}\" is not declared");
                continue;
            }

            if (ns.NamespaceName == Blend || IsIgnored(element, ns))
            {
                continue;
            }

            if (ns.NamespaceName == XamlNamespace)
            {
                var argument = positional.FirstOrDefault() ?? named.FirstOrDefault(n => n.Key is "Member" or "TypeName" or "Type").Value;
                if (local == "Static" && argument is not null)
                {
                    var dot = argument.LastIndexOf('.');
                    var owner = dot < 0 ? null : ResolveTypeName(element, argument[..dot]);
                    if (owner is null || !HasStaticMember(owner, argument[(dot + 1)..]))
                    {
                        problems.Add($"{file}({line}): {{x:Static {argument}}} does not exist");
                    }
                }
                else if (local == "Type" && argument is not null && ResolveTypeName(element, argument) is null)
                {
                    problems.Add($"{file}({line}): {{x:Type {argument}}} does not exist");
                }

                continue;
            }

            var type = ResolveType(ns, local + "Extension") ?? ResolveType(ns, local);
            if (type is null)
            {
                problems.Add($"{file}({line}): {{{extension}}} is not a markup extension this XAML can find");
                continue;
            }

            foreach (var (key, argument) in named)
            {
                var propertyType = PropertyType(type, key);
                if (propertyType is null)
                {
                    problems.Add($"{file}({line}): {{{extension} {key}=…}}: {type.Name} has no such property");
                }
                else if (!IsMarkup(argument) && Problem(propertyType, argument) is { } problem)
                {
                    problems.Add($"{file}({line}): {{{extension} {key}={argument}}}: {problem}");
                }
            }

            if (positional.Count > 0)
            {
                var constructors = type.GetConstructors().Where(c => c.GetParameters().Length == positional.Count).ToList();
                if (constructors.Count == 1)
                {
                    var parameters = constructors[0].GetParameters();
                    for (var i = 0; i < parameters.Length; i++)
                    {
                        if (!IsMarkup(positional[i]) && Problem(parameters[i].ParameterType, positional[i]) is { } problem)
                        {
                            problems.Add($"{file}({line}): {{{extension} {positional[i]}}}: {problem}");
                        }
                    }
                }
            }
        }
    }

    /// <summary>Every markup extension of a value, nested ones included: its name, positional and named arguments.</summary>
    private static IEnumerable<(string Extension, List<string> Positional, List<KeyValuePair<string, string>> Named)> MarkupExtensions(string value)
    {
        var text = value.Trim();
        if (!text.StartsWith('{') || text.StartsWith("{}", StringComparison.Ordinal) || !text.EndsWith('}'))
        {
            yield break;
        }

        var body = text[1..^1].Trim();
        var space = body.IndexOfAny([' ', '\t', '\r', '\n']);
        var extension = space < 0 ? body : body[..space];
        var rest = space < 0 ? string.Empty : body[(space + 1)..];
        var positional = new List<string>();
        var named = new List<KeyValuePair<string, string>>();
        foreach (var part in SplitArguments(rest))
        {
            var equals = TopLevelEquals(part);
            if (equals > 0)
            {
                var argument = part[(equals + 1)..].Trim();
                named.Add(new(part[..equals].Trim(), Unquote(argument)));
                foreach (var nested in MarkupExtensions(argument))
                {
                    yield return nested;
                }
            }
            else
            {
                positional.Add(Unquote(part.Trim()));
                foreach (var nested in MarkupExtensions(part))
                {
                    yield return nested;
                }
            }
        }

        yield return (extension, positional, named);
    }

    private static List<string> SplitArguments(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var quote = '\0';
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }

        if (text[start..].Trim().Length > 0)
        {
            parts.Add(text[start..]);
        }

        return parts.Where(p => p.Trim().Length > 0).ToList();
    }

    private static int TopLevelEquals(string part)
    {
        for (var i = 0; i < part.Length; i++)
        {
            if (part[i] is '{' or '\'' or '"')
            {
                return -1;
            }

            if (part[i] == '=')
            {
                return i;
            }
        }

        return -1;
    }

    private static string Unquote(string text)
        => text.Length >= 2 && (text[0] is '\'' or '"') && text[^1] == text[0] ? text[1..^1] : text;

    private static bool IsMarkup(string value)
    {
        var text = value.TrimStart();
        return text.StartsWith('{') && !text.StartsWith("{}", StringComparison.Ordinal);
    }

    /// <summary>Why a literal does not convert to a property's type (enums and booleans), or null.</summary>
    private static string? Problem(Type type, string value)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == "System.Nullable`1")
        {
            type = type.GetGenericArguments()[0];
        }

        if (type.FullName == "System.Boolean")
        {
            return bool.TryParse(value.Trim(), out _) ? null : "not True or False";
        }

        // An enum with a converter of its own (ModifierKeys: "Control+Shift", Key) reads more than its names.
        if (!type.IsEnum || type.GetCustomAttributesData().Any(a => a.AttributeType.FullName == "System.ComponentModel.TypeConverterAttribute"))
        {
            return null;
        }

        var names = type.GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var token in value.Split(','))
        {
            var member = token.Trim();
            if (!names.Contains(member) && !long.TryParse(member, out _))
            {
                return $"\"{member}\" is not a member of {type.Name}";
            }
        }

        return null;
    }

    private static Type? PropertyType(Type type, string name)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            var property = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (property is not null)
            {
                return property.PropertyType;
            }
        }

        return null;
    }

    private static Type? AttachedPropertyType(Type owner, string name)
    {
        for (var t = owner; t is not null; t = t.BaseType)
        {
            var getter = t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == "Get" + name && m.GetParameters().Length == 1);
            if (getter is not null)
            {
                return getter.ReturnType;
            }
        }

        return null;
    }

    private static object? Event(Type type, string name)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t.GetEvent(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) is { } e)
            {
                return e;
            }

            // Attached events (Mouse.PreviewMouseDown…) have Add…Handler methods.
            if (t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).FirstOrDefault(m => m.Name == "Add" + name + "Handler") is { } adder)
            {
                return adder;
            }
        }

        return null;
    }

    private static bool HasStaticMember(Type type, string name)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
            if (t.GetField(name, flags) is not null || t.GetProperty(name, flags) is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A type written as "prefix:Name", "Name" or "{x:Type …}" in the scope of an element.</summary>
    private Type? ResolveTypeName(XElement scope, string typeName)
    {
        var text = typeName.Trim();
        foreach (var (extension, positional, named) in MarkupExtensions(text))
        {
            if (extension is "x:Type" && (positional.FirstOrDefault() ?? named.FirstOrDefault().Value) is { } inner)
            {
                text = inner;
            }
        }

        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var ns = colon < 0 ? scope.GetDefaultNamespace() : scope.GetNamespaceOfPrefix(text[..colon]);
        return ns is null ? null : ResolveType(ns, colon < 0 ? text : text[(colon + 1)..]);
    }

    private Type? ResolveType(XNamespace ns, string name)
    {
        var key = ns.NamespaceName + "|" + name;
        if (_types.TryGetValue(key, out var known))
        {
            return known;
        }

        Type? found = null;
        var uri = ns.NamespaceName;
        if (uri.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            var parts = uri["clr-namespace:".Length..].Split(';');
            var assemblyPart = parts.Skip(1).FirstOrDefault(p => p.StartsWith("assembly=", StringComparison.Ordinal));
            var assembly = assemblyPart is null ? _app : Load(assemblyPart["assembly=".Length..]);
            found = assembly?.GetType(parts[0] + "." + name);
        }
        else if (_xmlns.TryGetValue(uri, out var namespaces))
        {
            found = namespaces.Select(n => n.Assembly.GetType(n.Namespace + "." + name)).FirstOrDefault(t => t is not null);
        }

        _types[key] = found;
        return found;
    }

    private Assembly? Load(string name)
    {
        try
        {
            return _load(name);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (FileLoadException)
        {
            return null;
        }
    }

    /// <summary>Prefixes the root marks as ignorable (mc:Ignorable="d").</summary>
    private static bool IsIgnored(XElement element, XNamespace ns)
    {
        if (ns == XNamespace.None)
        {
            return false;
        }

        var ignorable = (string?)element.AncestorsAndSelf().Select(e => e.Attribute(XName.Get("Ignorable", Compatibility))).LastOrDefault(a => a is not null);
        if (ignorable is null)
        {
            return false;
        }

        return ignorable.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(prefix => element.GetNamespaceOfPrefix(prefix) == ns);
    }
}
