using System.Text.Json;

namespace Dormouse;

// An effect result that was produced outside of Run and sent to the flow as a message.
//
// Result carries the type as well as the payload - length-prefixed the same way a FlowState
// entry is - because the flow has nothing else to go on: JSON does not say what it was written
// from, and the effect has to be recorded under the type it was captured as.
public record Captured(int Id, byte[] Result)
{
    // The counterpart to Capture's own recording, so a sender does not have to hand-roll the
    // framing: the payload is serialized against the static type T, and that is the type
    // written alongside it.
    public static Captured Create<T>(int id, T value)
        => new(id, ByteArrayMarshaller.Serialize(typeof(T).SerializeType(), JsonSerializer.SerializeToUtf8Bytes(value)));

    public (Type Type, byte[] Payload) Decode()
    {
        var fields = ByteArrayMarshaller.Deserialize(Result, expectedCount: 2);

        return (fields[0]!.Value.ToArray().ResolveType()!, fields[1]!.Value.ToArray());
    }
}
