using System.Text;
using System.Xml.Linq;

namespace Podish.Wayland.Generator;

internal sealed record GeneratorOptions(
    string OutputPath,
    IReadOnlyDictionary<string, uint> SupportedInterfaces,
    IReadOnlyList<string> XmlPaths);

internal sealed record ProtocolModel(string Name, List<InterfaceModel> Interfaces, string SourcePath);

internal sealed record InterfaceModel(
    string Name,
    uint DeclaredVersion,
    uint SupportedVersion,
    List<MessageModel> Requests,
    List<MessageModel> Events,
    List<EnumModel> Enums);

internal sealed record MessageModel(string Name, ushort Opcode, uint Since, List<ArgModel> Args);

internal sealed record ArgModel(
    string Name,
    string Type,
    string? Interface,
    bool AllowNull,
    string? Enum);

internal sealed record EnumModel(string Name, List<EnumEntryModel> Entries);
internal sealed record EnumEntryModel(string Name, string Value);

internal static class Program
{
    private static readonly HashSet<string> KnownEnumTypes = [];

    private static readonly IReadOnlyDictionary<string, uint> DefaultInterfaces = new Dictionary<string, uint>(StringComparer.Ordinal)
    {
        ["wl_display"] = 1,
        ["wl_registry"] = 1,
        ["wl_callback"] = 1,
        ["wl_compositor"] = 4,
        ["wl_surface"] = 4,
        ["wl_region"] = 1,
        ["wl_subcompositor"] = 1,
        ["wl_subsurface"] = 1,
        ["wl_shm"] = 1,
        ["wl_shm_pool"] = 1,
        ["wl_buffer"] = 1,
        ["wl_data_offer"] = 3,
        ["wl_data_source"] = 3,
        ["wl_data_device"] = 3,
        ["wl_data_device_manager"] = 3,
        ["wl_seat"] = 7,
        ["wl_pointer"] = 7,
        ["wl_keyboard"] = 7,
        ["wl_output"] = 4,
        ["xdg_wm_base"] = 1,
        ["xdg_surface"] = 1,
        ["xdg_toplevel"] = 1,
    };

    private static int Main(string[] args)
    {
        GeneratorOptions options = ParseArgs(args);
        string repoRoot = FindRepoRoot();
        string outputPath = Path.IsPathRooted(options.OutputPath)
            ? options.OutputPath
            : Path.Combine(repoRoot, options.OutputPath);

        List<ProtocolModel> protocols = options.XmlPaths.Select(path => LoadProtocol(path, options.SupportedInterfaces)).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? repoRoot);
        File.WriteAllText(outputPath, RenderCoreProtocols(protocols));
        Console.WriteLine($"Generated {outputPath}");
        return 0;
    }

    private static GeneratorOptions ParseArgs(string[] args)
    {
        string outputPath = Path.Combine("Podish.Wayland", "Generated", "CoreProtocols.g.cs");
        var xmlPaths = new List<string>();
        var supportedInterfaces = new Dictionary<string, uint>(DefaultInterfaces, StringComparer.Ordinal);

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--output":
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--output requires a value.");
                    outputPath = args[++i];
                    break;
                case "--interface":
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--interface requires a value like wl_surface=4.");
                    ParseInterfaceOverride(args[++i], supportedInterfaces);
                    break;
                default:
                    xmlPaths.Add(args[i]);
                    break;
            }
        }

        if (xmlPaths.Count == 0)
        {
            throw new ArgumentException(
                "Usage: Podish.Wayland.Generator [--output <path>] [--interface <name=version>]... <protocol.xml> [protocol.xml...]");
        }

        return new GeneratorOptions(outputPath, supportedInterfaces, xmlPaths);
    }

    private static void ParseInterfaceOverride(string value, IDictionary<string, uint> supportedInterfaces)
    {
        string[] parts = value.Split('=', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !uint.TryParse(parts[1], out uint version))
            throw new ArgumentException($"Invalid interface override '{value}'. Expected <name=version>.");

        supportedInterfaces[parts[0]] = version;
    }

    private static ProtocolModel LoadProtocol(string xmlPath, IReadOnlyDictionary<string, uint> supportedInterfaces)
    {
        var document = XDocument.Load(xmlPath);
        XElement protocolElement = document.Root ?? throw new InvalidDataException("Protocol XML is missing a root.");
        string protocolName = protocolElement.Attribute("name")?.Value ?? Path.GetFileNameWithoutExtension(xmlPath);
        var interfaces = new List<InterfaceModel>();

        foreach (XElement iface in protocolElement.Elements("interface"))
        {
            string ifaceName = iface.Attribute("name")?.Value ?? throw new InvalidDataException("Interface without name.");
            if (!supportedInterfaces.TryGetValue(ifaceName, out uint supportedVersion))
                continue;

            uint declaredVersion = uint.Parse(iface.Attribute("version")?.Value ?? "1");
            uint effectiveVersion = Math.Min(supportedVersion, declaredVersion);
            List<MessageModel> requests = ReadMessages(iface, ifaceName, "request", effectiveVersion);
            List<MessageModel> events = ReadMessages(iface, ifaceName, "event", effectiveVersion);
            List<EnumModel> enums = iface.Elements("enum")
                .Select(ReadEnum)
                .ToList();

            interfaces.Add(new InterfaceModel(ifaceName, declaredVersion, effectiveVersion, requests, events, enums));
        }

        return new ProtocolModel(protocolName, interfaces, xmlPath);
    }

    private static EnumModel ReadEnum(XElement element)
    {
        string enumName = element.Attribute("name")?.Value ?? "unnamed";
        List<EnumEntryModel> entries = element.Elements("entry")
            .Select(entry => new EnumEntryModel(
                entry.Attribute("name")?.Value ?? "entry",
                entry.Attribute("value")?.Value ?? "0"))
            .ToList();
        return new EnumModel(enumName, entries);
    }

    private static List<MessageModel> ReadMessages(XElement iface, string interfaceName, string elementName, uint supportedVersion)
    {
        var messages = new List<MessageModel>();
        ushort opcode = 0;
        foreach (XElement message in iface.Elements(elementName))
        {
            uint since = uint.Parse(message.Attribute("since")?.Value ?? "1");
            if (since > supportedVersion)
            {
                opcode++;
                continue;
            }

            string name = message.Attribute("name")?.Value ?? throw new InvalidDataException($"Missing {elementName} name.");
            List<ArgModel> args = message.Elements("arg")
                .SelectMany(arg => ExpandArg(interfaceName, elementName, arg))
                .ToList();
            messages.Add(new MessageModel(name, opcode, since, args));
            opcode++;
        }

        return messages;
    }

    private static IEnumerable<ArgModel> ExpandArg(string interfaceName, string elementName, XElement arg)
    {
        string name = arg.Attribute("name")?.Value ?? "arg";
        string type = arg.Attribute("type")?.Value ?? "uint";
        string? targetInterface = arg.Attribute("interface")?.Value;
        bool allowNull = string.Equals(arg.Attribute("allow-null")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        string? enumRef = NormalizeEnumReference(interfaceName, arg.Attribute("enum")?.Value);

        if (elementName == "request" && type == "new_id" && targetInterface == null)
        {
            yield return new ArgModel("interface", "string", null, false, null);
            yield return new ArgModel("version", "uint", null, false, null);
            yield return new ArgModel(name, "new_id", null, allowNull, enumRef);
            yield break;
        }

        yield return new ArgModel(name, type, targetInterface, allowNull, enumRef);
    }

    private static string RenderCoreProtocols(IReadOnlyList<ProtocolModel> protocols)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Generated by Podish.Wayland.Generator");
        foreach (ProtocolModel protocol in protocols)
            sb.AppendLine($"// Source protocol: {protocol.Name} ({protocol.SourcePath})");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("namespace Podish.Wayland;");
        sb.AppendLine();

        KnownEnumTypes.Clear();
        HashSet<string> emittedEnums = [];
        foreach (InterfaceModel iface in protocols.SelectMany(protocol => protocol.Interfaces))
        {
            foreach (EnumModel enumModel in iface.Enums)
            {
                string enumTypeName = GetEnumTypeName(iface.Name, enumModel.Name);
                if (!emittedEnums.Add(enumTypeName))
                    continue;

                KnownEnumTypes.Add(enumTypeName);
                sb.AppendLine($"public enum {enumTypeName} : uint");
                sb.AppendLine("{");
                foreach (EnumEntryModel entry in enumModel.Entries)
                    sb.AppendLine($"    {ToPascalCase(entry.Name)} = {entry.Value},");
                sb.AppendLine("}");
                sb.AppendLine();
            }
        }

        foreach (InterfaceModel iface in protocols.SelectMany(protocol => protocol.Interfaces))
            RenderInterface(sb, iface);

        return sb.ToString();
    }

    private static void RenderInterface(StringBuilder sb, InterfaceModel iface)
    {
        string interfaceType = ToPascalCase(iface.Name);
        sb.AppendLine($"public static class {interfaceType}Protocol");
        sb.AppendLine("{");
        sb.AppendLine($"    public const string InterfaceName = \"{iface.Name}\";");
        sb.AppendLine($"    public const uint Version = {iface.SupportedVersion};");
        sb.AppendLine();
        sb.AppendLine("    public static readonly ReadOnlyCollection<WaylandMessageMetadata> Requests = new([");
        for (var i = 0; i < iface.Requests.Count; i++)
        {
            MessageModel request = iface.Requests[i];
            sb.Append("        ");
            RenderMessageMetadata(sb, iface.Name, request);
            sb.AppendLine(i == iface.Requests.Count - 1 ? string.Empty : ",");
        }
        sb.AppendLine("    ]);");
        sb.AppendLine();

        foreach (MessageModel request in iface.Requests.Where(ShouldEmitDecoder))
        {
            string recordType = GetRequestRecordTypeName(iface.Name, request.Name);
            string methodName = $"Decode{ToPascalCase(request.Name)}";

            sb.AppendLine($"    public static {recordType} {methodName}(byte[] body, IReadOnlyList<LinuxFile> fds)");
            sb.AppendLine("    {");
            sb.AppendLine("        var reader = new WaylandWireReader(body, fds);");

            if (request.Args.Count == 0)
            {
                sb.AppendLine("        reader.EnsureExhausted();");
                sb.AppendLine($"        return new {recordType}();");
            }
            else
            {
                for (var i = 0; i < request.Args.Count; i++)
                {
                    ArgModel arg = request.Args[i];
                    sb.AppendLine($"        {GetClrType(arg)} {GetLocalName(i)} = {GetReadExpression(arg)};");
                }

                sb.AppendLine($"        var request = new {recordType}({string.Join(", ", Enumerable.Range(0, request.Args.Count).Select(GetLocalName))});");
                sb.AppendLine("        reader.EnsureExhausted();");
                sb.AppendLine("        return request;");
            }

            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        sb.AppendLine();

        foreach (MessageModel request in iface.Requests.Where(ShouldEmitDecoder))
        {
            string recordType = GetRequestRecordTypeName(iface.Name, request.Name);
            sb.AppendLine($"public readonly record struct {recordType}({string.Join(", ", request.Args.Select(RenderRecordField))});");
        }
        if (iface.Requests.Any(ShouldEmitDecoder))
            sb.AppendLine();

        if (iface.Events.Count > 0)
        {
            sb.AppendLine($"public static class {interfaceType}EventWriter");
            sb.AppendLine("{");
            foreach (MessageModel evt in iface.Events)
            {
                string methodName = $"{ToPascalCase(evt.Name)}Async";
                string parameters = evt.Args.Count == 0
                    ? "WaylandClient client, uint objectId"
                    : $"WaylandClient client, uint objectId, {string.Join(", ", evt.Args.Select(RenderMethodParameter))}";

                sb.AppendLine($"    public static ValueTask {methodName}({parameters})");
                sb.AppendLine("    {");
                if (evt.Args.Count == 0)
                {
                    sb.AppendLine($"        return client.SendEventAsync(objectId, {evt.Opcode}, static _ => {{ }});");
                }
                else
                {
                    sb.AppendLine($"        return client.SendEventAsync(objectId, {evt.Opcode}, writer =>");
                    sb.AppendLine("        {");
                    foreach (ArgModel arg in evt.Args)
                        sb.AppendLine($"            {GetWriteStatement("writer", arg)}");
                    sb.AppendLine("        });");
                }
                sb.AppendLine("    }");
                sb.AppendLine();
            }
            sb.AppendLine("}");
            sb.AppendLine();
        }
    }

    private static bool ShouldEmitDecoder(MessageModel message) => message.Args.Count > 0;

    private static void RenderMessageMetadata(StringBuilder sb, string interfaceName, MessageModel message)
    {
        sb.Append($"new WaylandMessageMetadata(InterfaceName, \"{message.Name}\", {message.Opcode}, {message.Since}, [");
        if (message.Args.Count > 0)
        {
            sb.AppendLine();
            for (var i = 0; i < message.Args.Count; i++)
            {
                ArgModel arg = message.Args[i];
                sb.Append("            ");
                sb.Append($"new WaylandArgumentMetadata(\"{arg.Name}\", {GetArgKind(arg)}");
                if (arg.Interface != null || arg.AllowNull)
                {
                    sb.Append($", {(arg.Interface == null ? "null" : $"\"{arg.Interface}\"")}");
                    if (arg.AllowNull)
                        sb.Append(", true");
                }
                sb.Append(")");
                sb.AppendLine(i == message.Args.Count - 1 ? string.Empty : ",");
            }
            sb.Append("        ");
        }
        sb.Append("])");
    }

    private static string GetArgKind(ArgModel arg)
    {
        return arg.Type switch
        {
            "int" => "WaylandArgKind.Int",
            "uint" => "WaylandArgKind.Uint",
            "fixed" => "WaylandArgKind.Fixed",
            "string" => "WaylandArgKind.String",
            "object" => "WaylandArgKind.Object",
            "new_id" => "WaylandArgKind.NewId",
            "array" => "WaylandArgKind.Array",
            "fd" => "WaylandArgKind.Fd",
            _ => throw new NotSupportedException($"Unsupported Wayland argument type '{arg.Type}'.")
        };
    }

    private static string GetRequestRecordTypeName(string interfaceName, string messageName)
    {
        return $"{ToPascalCase(interfaceName)}{ToPascalCase(messageName)}Request";
    }

    private static string GetEnumTypeName(string interfaceName, string enumName)
    {
        return $"{ToPascalCase(interfaceName)}{ToPascalCase(enumName)}";
    }

    private static string RenderRecordField(ArgModel arg)
    {
        return $"{GetClrType(arg)} {ToPascalCase(arg.Name)}";
    }

    private static string RenderMethodParameter(ArgModel arg)
    {
        return $"{GetClrType(arg)} {GetParameterName(arg)}";
    }

    private static string GetClrType(ArgModel arg)
    {
        if (arg.Enum != null)
        {
            string enumType = GetEnumTypeNameFromRef(arg.Enum);
            if (KnownEnumTypes.Contains(enumType))
                return enumType;
        }

        return arg.Type switch
        {
            "int" => "int",
            "uint" => "uint",
            "fixed" => "int",
            "string" => arg.AllowNull ? "string?" : "string",
            "object" => "uint",
            "new_id" => "uint",
            "array" => "byte[]",
            "fd" => "LinuxFile",
            _ => throw new NotSupportedException($"Unsupported Wayland argument type '{arg.Type}'.")
        };
    }

    private static string GetReadExpression(ArgModel arg)
    {
        string read = arg.Type switch
        {
            "int" => "reader.ReadInt()",
            "uint" => "reader.ReadUInt()",
            "fixed" => "reader.ReadFixed()",
            "string" => arg.AllowNull ? "reader.ReadString()" : "reader.ReadString() ?? string.Empty",
            "object" => "reader.ReadObjectId()",
            "new_id" => "reader.ReadNewId()",
            "array" => "reader.ReadArray()",
            "fd" => "reader.ReadFd()",
            _ => throw new NotSupportedException($"Unsupported Wayland argument type '{arg.Type}'.")
        };

        if (arg.Enum == null)
            return read;

        string enumType = GetEnumTypeNameFromRef(arg.Enum);
        if (!KnownEnumTypes.Contains(enumType))
            return read;

        return arg.Type switch
        {
            "int" => $"({enumType}){read}",
            "uint" => $"({enumType}){read}",
            _ => throw new NotSupportedException($"Enum-backed Wayland argument '{arg.Name}' must be int or uint.")
        };
    }

    private static string GetWriteStatement(string writerName, ArgModel arg)
    {
        string valueName = GetParameterName(arg);
        if (arg.Enum != null)
        {
            string enumType = GetEnumTypeNameFromRef(arg.Enum);
            if (!KnownEnumTypes.Contains(enumType))
            {
                return arg.Type switch
                {
                    "int" => $"{writerName}.WriteInt({valueName});",
                    "uint" => $"{writerName}.WriteUInt({valueName});",
                    _ => throw new NotSupportedException($"Enum-backed Wayland event argument '{arg.Name}' must be int or uint.")
                };
            }

            return arg.Type switch
            {
                "int" => $"{writerName}.WriteInt((int){valueName});",
                "uint" => $"{writerName}.WriteUInt((uint){valueName});",
                _ => throw new NotSupportedException($"Enum-backed Wayland event argument '{arg.Name}' must be int or uint.")
            };
        }

        return arg.Type switch
        {
            "int" => $"{writerName}.WriteInt({valueName});",
            "uint" => $"{writerName}.WriteUInt({valueName});",
            "fixed" => $"{writerName}.WriteFixed({valueName});",
            "string" => $"{writerName}.WriteString({valueName});",
            "object" => $"{writerName}.WriteObjectId({valueName});",
            "new_id" => $"{writerName}.WriteNewId({valueName});",
            "array" => $"{writerName}.WriteArray({valueName});",
            "fd" => $"{writerName}.WriteFd({valueName});",
            _ => throw new NotSupportedException($"Unsupported Wayland event argument type '{arg.Type}'.")
        };
    }

    private static string GetEnumTypeNameFromRef(string enumRef)
    {
        string[] parts = enumRef.Split('.', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            throw new InvalidDataException($"Unsupported enum reference '{enumRef}'. Expected <interface>.<enum>.");
        return GetEnumTypeName(parts[0], parts[1]);
    }

    private static string? NormalizeEnumReference(string interfaceName, string? enumRef)
    {
        if (string.IsNullOrEmpty(enumRef))
            return enumRef;

        return enumRef.Contains('.', StringComparison.Ordinal) ? enumRef : $"{interfaceName}.{enumRef}";
    }

    private static string GetLocalName(int index) => $"arg{index}";

    private static string ToPascalCase(string value)
    {
        return string.Concat(value.Split('_', '-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                string identifier = ToIdentifier(part);
                return char.ToUpperInvariant(identifier[0]) + identifier[1..];
            }));
    }

    private static string ToCamelCase(string value)
    {
        string pascal = ToPascalCase(value);
        return char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }

    private static string GetParameterName(ArgModel arg)
    {
        string name = ToCamelCase(arg.Name);
        return name switch
        {
            "client" => "argClient",
            "class" => "argClass",
            "event" => "argEvent",
            "interface" => "argInterface",
            "namespace" => "argNamespace",
            "objectId" => "argObjectId",
            "operator" => "argOperator",
            "params" => "argParams",
            "string" => "argString",
            "writer" => "argWriter",
            "request" => "argRequest",
            _ => name
        };
    }

    private static string ToIdentifier(string value)
    {
        string sanitized = value.Replace("-", "_");
        if (string.IsNullOrEmpty(sanitized))
            return "_";
        if (char.IsDigit(sanitized[0]))
            sanitized = "_" + sanitized;
        return sanitized;
    }

    private static string FindRepoRoot()
    {
        string current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Fiberish.sln")))
                return current;
            current = Path.GetDirectoryName(current) ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Could not locate repo root containing Fiberish.sln.");
    }
}
