namespace Dormouse;

using System;
using System.Collections.Concurrent;
using System.Text;

// The name a Type is written under in a flow's state, and the Type that name resolves back to,
// each behind a cache. An instance rather than a static so that the caches belong to the
// DormouseContext that hands it out: nothing is process-wide, and every test gets a fresh one.
public sealed class TypeHelper
{
    private readonly ConcurrentDictionary<Type, string> _simpleQualifiedNames = new();
    private readonly ConcurrentDictionary<string, Type> _resolvedTypes = new();

    public byte[] SerializeType(Type type) => SimpleQualifiedName(type).ToUtf8Bytes();

    // Only names that resolve are cached: throwOnError makes Type.GetType throw rather than return
    // null, and GetOrAdd does not store a value when the factory throws. So unresolvable names keep
    // throwing on every call (unchanged behaviour) and cannot grow the cache.
    //
    // Takes the marshalled segment as it comes: the cache keys on the decoded string, so there is
    // no reason to copy the bytes out to an array of their own first.
    public Type ResolveType(ReadOnlyMemory<byte> serializedType)
        => _resolvedTypes.GetOrAdd(
            serializedType.ToStringFromUtf8Bytes(),
            static name => Type.GetType(name, throwOnError: true)!);

    public string SimpleQualifiedName(Type type)
        => _simpleQualifiedNames.GetOrAdd(type, static t =>
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
    //
    // Static because it is pure - a string in, a builder written to - and so the factory
    // above can stay a static lambda that captures nothing.
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
