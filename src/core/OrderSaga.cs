using Microsoft.Extensions.Logging;
using Wolverine;

namespace Dormouse;

// The message types now live in their own files; this is the original hand-written
// saga over them, commented out so FlowSaga is the only saga in the application.
/*
public class Order : Saga
{
    // The saga identity. Wolverine uses this to load and delete the saga state.
    public string? Id { get; set; }

    public string Item { get; set; } = string.Empty;
    public bool Paid { get; set; }
    public bool Shipped { get; set; }

    // "Start" is only called when the saga does not exist yet. The returned Order
    // becomes the persisted saga state, and the OrderTimeout is scheduled for later.
    public static (Order, OrderTimeout) Start(StartOrder start, ILogger<Order> logger)
    {
        logger.LogInformation("Started order {Id} for {Item}", start.OrderId, start.Item);

        return (
            new Order { Id = start.OrderId, Item = start.Item },
            new OrderTimeout(start.OrderId)
        );
    }

    // "Handle" is only called when the saga already exists. Wolverine loads the
    // state, runs this method, then saves the mutated state back.
    public void Handle(PaymentReceived payment, ILogger<Order> logger)
    {
        Paid = true;
        logger.LogInformation("Order {Id} paid with {Amount} EUR", Id, payment.Amount);
    }

    // Returning a message from a saga method publishes it as a cascading message.
    public OrderCompleted? Handle(OrderShipped shipped, ILogger<Order> logger)
    {
        Shipped = true;
        logger.LogInformation("Order {Id} shipped with tracking {Tracking}", Id, shipped.TrackingNumber);

        if (!Paid)
        {
            logger.LogWarning("Order {Id} shipped before payment arrived, waiting for payment", Id);
            return null;
        }

        // Tell Wolverine the workflow is finished so the saga state gets deleted.
        MarkCompleted();

        return new OrderCompleted(Id!);
    }

    // The timeout only reaches an order that is still in flight - for a completed
    // order the state is already gone and Wolverine quietly drops the timeout.
    //
    // This one is async on purpose: as of Wolverine 6.28.0, a synchronous handler
    // for a TimeoutMessage generates code that fails to compile at runtime with
    // "CS0126: An object of a type convertible to 'Task' is required".
    public Task HandleAsync(OrderTimeout timeout, ILogger<Order> logger)
    {
        logger.LogWarning("Order {Id} timed out (paid: {Paid}, shipped: {Shipped}), abandoning it",
            Id, Paid, Shipped);

        MarkCompleted();

        return Task.CompletedTask;
    }

    // Without this, Wolverine throws when a message arrives for a saga that does
    // not exist. (Timeout messages are the exception - they are dropped silently.)
    public static void NotFound(PaymentReceived payment, ILogger<Order> logger)
    {
        logger.LogWarning("Payment received for unknown order {Id}", payment.OrderId);
    }
}

// An ordinary Wolverine handler, outside of the saga, for the cascading message
// the saga publishes when the order is done.
public static class OrderCompletedHandler
{
    public static void Handle(OrderCompleted completed, ILogger<OrderCompleted> logger)
    {
        logger.LogInformation("Order {Id} is complete, sending the receipt", completed.OrderId);
    }
}
*/
