using System.Buffers.Binary;

namespace Dormouse;

// Flow keeps the encoding of its own state private - nothing outside the flow writes an entry,
// so there is no reason for Encode to be reachable. Reading one is a different matter: the demo
// prints what a flow recorded and the tests assert on it, and neither can get at Flow.Decode.
//
// This is that read side, and only that: the same four fields Flow.Encode lays down - the kind
// of entry, where it sits on that kind's own sequence, the type its payload was serialized as,
// and the payload.
public static class FlowStateReader
{
    public static FlowStateEntry Decode(byte[] entry)
    {
        var fields = ByteArrayMarshaller.Deserialize(entry, expectedCount: 4);

        return new FlowStateEntry(
            (StateType)fields[0]!.Value.Span[0],
            BinaryPrimitives.ReadInt32LittleEndian(fields[1]!.Value.Span),
            fields[2]!.Value.ToArray().ResolveType()!,
            fields[3]!.Value.ToArray()
        );
    }

    public static IEnumerable<FlowStateEntry> Read(Flow flow) => flow.FlowState.Select(Decode);

    public static IEnumerable<FlowStateEntry> Effects(Flow flow)
        => Read(flow).Where(e => e.StateType == StateType.Effect);

    // Every message the flow was handed, in the order it was recorded: the one that started it
    // is written as an InitialMessage entry, everything after it as a Message entry.
    public static IEnumerable<FlowStateEntry> Messages(Flow flow)
        => Read(flow).Where(e => e.StateType is StateType.InitialMessage or StateType.Message);
}
