using System.Buffers.Binary;

namespace Dormouse;

// Flow keeps the encoding of its own state private - nothing outside the flow writes an entry,
// so there is no reason for Encode to be reachable. Reading one is a different matter: the demo
// prints what a flow recorded and the tests assert on it, and neither can get at Flow.Decode.
//
// This is that read side, and only that: the same four fields Flow.Encode lays down - the kind
// of entry, where it sits on that kind's own sequence, the type its payload was serialized as,
// and the payload. Type names are resolved through the context's TypeHelper, the same one the
// flow wrote them with; a flow loaded straight from storage carries no context of its own, which
// is why the reader asks for one rather than taking it off the flow.
public static class FlowStateReader
{
    public static FlowStateEntry Decode(byte[] entry, DormouseContext context)
    {
        var fields = ByteArrayMarshaller.Deserialize(entry, expectedCount: 4);

        return new FlowStateEntry(
            (StateType)fields[0]!.Value.Span[0],
            BinaryPrimitives.ReadInt32LittleEndian(fields[1]!.Value.Span),
            context.TypeHelper.ResolveType(fields[2]!.Value),
            fields[3]!.Value.ToArray()
        );
    }

    public static IEnumerable<FlowStateEntry> Read(Flow flow, DormouseContext context)
        => flow.FlowState.Select(entry => Decode(entry, context));

    public static IEnumerable<FlowStateEntry> Effects(Flow flow, DormouseContext context)
        => Read(flow, context).Where(e => e.StateType == StateType.Effect);

    // Every message the flow was handed, in the order it was recorded: the one that started it
    // is written as an InitialMessage entry, everything after it as a Message entry.
    public static IEnumerable<FlowStateEntry> Messages(Flow flow, DormouseContext context)
        => Read(flow, context).Where(e => e.StateType is StateType.InitialMessage or StateType.Message);
}
