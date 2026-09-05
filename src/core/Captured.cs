using Wolverine.Persistence.Sagas;

namespace Dormouse;

// The signal that an effect completed outside of Run. It carries no result: the handler on Flow
// takes it as a discard and does no more than wake the flow up, which then reads what it needs
// out of its own recorded state.
//
// What it does have to carry is the identity of the flow it is for. Wolverine works the saga id
// out of the message before the handler is called, so a message with nothing on it to work from
// is one it refuses to build a saga chain for.
//
// Not tied to any of a flow's own message types, so every flow can be handed one whatever it is
// written against.
public record Captured([property: SagaIdentity] string FlowId);
