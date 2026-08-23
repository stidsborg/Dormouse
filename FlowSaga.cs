using Dormouse.Messages;

namespace Dormouse;

// Same message types as the Order saga, but every handler method is inherited
// from the generic Flow base class instead of being declared here.
public class FlowSaga : Flow<StartOrder, PaymentReceived, OrderShipped, OrderTimeout, OrderCompleted>
{
    protected override async Task Run(StartOrder message)
    {
        Console.WriteLine("Starting flow!!!");
        var id = await Capture(Guid.NewGuid);
        Console.WriteLine(id);
    }
}
