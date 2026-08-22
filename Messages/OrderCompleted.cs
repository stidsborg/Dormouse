using Wolverine.Persistence.Sagas;

namespace Dormouse.Messages;

public record OrderCompleted([property: SagaIdentity] string OrderId);
