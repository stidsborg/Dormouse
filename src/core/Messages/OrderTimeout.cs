using JasperFx.Core;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace Dormouse.Messages;

// Subclassing TimeoutMessage means this message is always scheduled to be
// delivered after the given delay instead of being handled immediately.
public record OrderTimeout([property: SagaIdentity] string OrderId) : TimeoutMessage(5.Seconds());
