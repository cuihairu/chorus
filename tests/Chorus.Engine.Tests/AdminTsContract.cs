using Google.Api;
using Google.Protobuf.Reflection;
using System.Text;

namespace Chorus.Engine.Tests;

/// <summary>
/// Renders the browser console's typed Admin client straight from the compiled descriptor
/// set (FIX-WEB-05). Every route is read from the google.api.http annotation, every message
/// interface from the request/reply closure of the annotated entries — the console never
/// speaks from a hand-maintained list, so a proto edit without regeneration fails the drift
/// gate (RedesignWeb05AdminClientContractTests) instead of compiling against a stale shape.
/// 64-bit integers surface as plain numbers — this server's JSON transcoding writes them
/// as JSON numbers, not the proto3-JSON decimal-string form (pinned against the live
/// endpoint by the FIX-WEB-03 host witness on appliedTick); singular message fields render
/// optional because proto3 JSON omits unset presence fields.
/// Field types the generator does not map fail loudly — expanding the mapping is deliberate.
/// </summary>
public static class AdminTsContract
{
    private const string Header =
        """
        // Generated from src/Chorus.Rpc/Protos/m3.proto by the FIX-WEB-05 contract generator
        // (tests/Chorus.Engine.Tests/AdminTsContract.cs). Do not edit by hand: regenerate with
        // CHORUS_REGEN_ADMIN_TS=1 dotnet test --filter "FullyQualifiedName~RedesignWeb05AdminClientContractTests"
        // and commit the result; the CI gate re-renders from the descriptors and refuses drift.
        """;

    public static string Render()
    {
        var admin = Chorus.Rpc.M3Reflection.Descriptor.Services
            .Single(s => s.FullName == "chorus.m3.Admin");
        var annotated = admin.Methods
            .Where(m => m.GetOptions()?.HasExtension(AnnotationsExtensions.Http) == true)
            .ToList();

        var messages = MessageClosure(annotated);

        var buffer = new StringBuilder();
        buffer.AppendLine(Header);

        foreach (var message in messages.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            RenderMessage(buffer, message);
        }

        RenderRoutes(buffer, annotated);
        RenderClient(buffer, annotated);
        return buffer.ToString();
    }

    private static List<MessageDescriptor> MessageClosure(List<MethodDescriptor> annotated)
    {
        var messages = new List<MessageDescriptor>();
        var seen = new HashSet<string>();
        var pending = new Queue<MessageDescriptor>(annotated
            .SelectMany(m => new[] { m.InputType, m.OutputType }));
        while (pending.Count > 0)
        {
            var message = pending.Dequeue();
            if (!seen.Add(message.FullName))
            {
                continue;
            }

            if (messages.Any(existing => existing.Name == message.Name))
            {
                throw new InvalidOperationException(
                    $"two contract messages share the TS name {message.Name}: {message.FullName}");
            }

            messages.Add(message);
            foreach (var field in message.Fields.InDeclarationOrder())
            {
                if (field.FieldType == FieldType.Message)
                {
                    pending.Enqueue(field.MessageType!);
                }
            }
        }

        return messages;
    }

    private static void RenderMessage(StringBuilder buffer, MessageDescriptor message)
    {
        buffer.AppendLine();
        buffer.AppendLine($"export interface {message.Name} {{");
        foreach (var field in message.Fields.InDeclarationOrder())
        {
            var optional = field.FieldType == FieldType.Message && !field.IsRepeated ? "?" : "";
            buffer.AppendLine($"  {field.JsonName}{optional}: {TsType(field)};");
        }

        buffer.AppendLine("}");
    }

    private static string TsType(FieldDescriptor field)
    {
        if (field.IsMap)
        {
            throw new InvalidOperationException(
                $"the Admin contract uses a map field the generator does not map: {field.FullName}");
        }

        return field.IsRepeated ? $"{TsScalar(field)}[]" : TsScalar(field);
    }

    private static string TsScalar(FieldDescriptor field) => field.FieldType switch
    {
        FieldType.String => "string",
        FieldType.Bool => "boolean",
        FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32
            or FieldType.UInt32 or FieldType.Fixed32
            or FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64
            or FieldType.UInt64 or FieldType.Fixed64 => "number",
        FieldType.Message => field.MessageType!.Name,
        _ => throw new InvalidOperationException(
            $"the Admin contract uses a field type the generator does not map: {field.FullName} ({field.FieldType})"),
    };

    private static void RenderRoutes(StringBuilder buffer, List<MethodDescriptor> annotated)
    {
        buffer.AppendLine();
        buffer.AppendLine("// The route table, read from the google.api.http annotations.");
        buffer.AppendLine("export const adminRoutes = {");
        foreach (var method in annotated)
        {
            var rule = method.GetOptions()!.GetExtension(AnnotationsExtensions.Http);
            buffer.AppendLine($"  {Camel(method.Name)}: '{rule.Post}',");
        }

        buffer.AppendLine("} as const;");
        buffer.AppendLine();
        buffer.AppendLine("export type AdminRouteName = keyof typeof adminRoutes;");
    }

    private static void RenderClient(StringBuilder buffer, List<MethodDescriptor> annotated)
    {
        buffer.AppendLine();
        buffer.AppendLine("// The typed client over the route table; the transport is injected so the");
        buffer.AppendLine("// browser core owns the credential header and the error mapping.");
        buffer.AppendLine("export class AdminClient {");
        buffer.AppendLine("  constructor(private readonly call: <Req, Rep>(path: string, request: Req) => Promise<Rep>) {}");
        foreach (var method in annotated)
        {
            buffer.AppendLine(
                $"  {Camel(method.Name)}(request: {method.InputType.Name}): Promise<{method.OutputType.Name}> {{");
            buffer.AppendLine(
                $"    return this.call<{method.InputType.Name}, {method.OutputType.Name}>(");
            buffer.AppendLine($"      adminRoutes.{Camel(method.Name)}, request);");
            buffer.AppendLine("  }");
        }

        buffer.AppendLine("}");
    }

    private static string Camel(string rpcName) =>
        char.ToLowerInvariant(rpcName[0]) + rpcName[1..];

    public static string FindCommittedPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "console", "src", "api")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new InvalidOperationException("console/src/api not found above the test working directory")
            : Path.Combine(dir.FullName, "console", "src", "api", "contract.ts");
    }
}
