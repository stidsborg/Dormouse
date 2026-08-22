using Wolverine.Persistence.Sagas;

namespace Dormouse.Messages;

public record OrderShipped([property: SagaIdentity] string OrderId, string TrackingNumber);
