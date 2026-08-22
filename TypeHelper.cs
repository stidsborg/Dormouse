namespace Dormouse;

using System;
using System.Collections.Concurrent;
using System.Text;

public static class TypeHelper
{
    private static readonly ConcurrentDictionary<Type, string> SimpleQualifiedNameCache = new();
    private static readonly ConcurrentDictionary<string, Type> ResolvedTypeCache = new();

    public static byte[] SerializeType(this Type type) => type.SimpleQualifiedName().ToUtf8Bytes();

    // Only names that resolve are cached: throwOnError makes Type.GetType throw rather than return
    // null, and GetOrAdd does not store a value when the factory throws. So unresolvable names keep
    // throwing on every call (unchanged behaviour) and cannot grow the cache.
    public static Type? ResolveType(this byte[] serializedType)
        => ResolvedTypeCache.GetOrAdd(
            serializedType.ToStringFromUtf8Bytes(),
            static name => Type.GetType(name, throwOnError: true)!);

    public static byte[] ToUtf8Bytes(this string str) => Encoding.UTF8.GetBytes(str);
    public static string ToStringFromUtf8Bytes(this byte[] bytes) => Encoding.UTF8.GetString(bytes);
    // Decodes a marshalled segment in place - ByteArrayMarshaller hands back views over the source
    // buffer, so this avoids copying each segment out to its own array first.
    public static string ToStringFromUtf8Bytes(this ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);

    public static string SimpleQualifiedName(this Type type)
        => SimpleQualifiedNameCache.GetOrAdd(type, static t =>
        {
            var assemblyQualifiedName = t.AssemblyQualifiedName;
            if (assemblyQualifiedName == null)
                return t.FullName ?? t.Name;

            // Reflection escapes ',', '+', '[', ']', '*', '&' and '\\' with a backslash when they occur
            // inside a type name rather than as syntax - which C# cannot produce, but F# quoted
            // identifiers, Reflection.Emit and obfuscators can. ExtractSimplifiedName walks the name
            // character by character and does not honour those escapes, so an escaped comma is counted
            // as an assembly qualifier and the assembly name is dropped from the result. Refuse the name
            // here instead of handing back one that no longer resolves. Throwing inside the factory also
            // keeps it out of the cache: GetOrAdd stores nothing when the factory throws.
            if (assemblyQualifiedName.Contains('\\'))
                throw new ArgumentException(
                    $"Type '{t}' cannot be simplified: its assembly-qualified name contains an escaped " +
                    $"character - {assemblyQualifiedName}",
                    nameof(type));

            var builder = new StringBuilder(assemblyQualifiedName.Length);
            ExtractSimplifiedName(assemblyQualifiedName, 0, builder);
            return builder.ToString();
        });

    // Walks one bracket-scope of an assembly-qualified name, copying it while dropping
    // the Version/Culture/PublicKeyToken segments. Recurses on '[' so each nested generic
    // argument (and array suffix) is processed in its own scope; returns the index of the
    // ']' that closed this scope so the caller can continue past it.
    private static int ExtractSimplifiedName(string name, int i, StringBuilder stringBuilder)
    {
        // Within a scope the assembly qualifier is "TypeName, Assembly, Version=.., Culture=.., PublicKeyToken=..".
        // The first "real" comma separates the type name from the assembly name (kept); any further commas
        // begin Version/Culture/Token (dropped). Commas of the form "],[" separate generic arguments and are
        // not assembly qualifiers, so they are excluded from the count via the lookahead below.
        var ignore = false;
        var commas = 0;
        for (; i < name.Length; i++)
        {
            var letter = name[i];
            if (letter == '[')
            {
                stringBuilder.Append('[');
                i = ExtractSimplifiedName(name, i + 1, stringBuilder);
                continue;
            }
            else if (letter == ']')
            {
                stringBuilder.Append(']');
                return i;
            }
            else if (letter == ',')
            {
                // A comma directly before '[' is a generic-argument separator ("],["), not an assembly qualifier.
                if (i + 1 < name.Length && name[i + 1] != '[')
                    commas++;
                ignore = commas > 1;
            }
            if (ignore) continue;

            stringBuilder.Append(letter);
        }

        return i;
    }
}