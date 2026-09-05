using Wolverine.Persistence.Sagas;

namespace Dormouse.Messages;

public record PaymentReceived([property: SagaIdentity] string OrderId, decimal Amount);
