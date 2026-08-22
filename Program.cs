using System.Text;
using Dormouse;
using Dormouse.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.SetMinimumLevel(LogLevel.Information);
// Wolverine's own logging is noisy for a demo like this
builder.Logging.AddFilter("Wolverine", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting", LogLevel.Warning);

// No persistence configured, so Wolverine falls back to in-memory saga storage.
// Swapping in Marten or EF Core here is all it takes to make the saga durable.
builder.Services.AddWolverine(opts =>
{
    // Where Wolverine scans for sagas and handlers
    opts.ApplicationAssembly = typeof(FlowSaga).Assembly;

    // Every flow inherits Handle(Captured), so the moment a second flow saga exists in the
    // assembly Wolverine refuses to start: two saga types handling one message type. Separated
    // gives each saga its own handler chain for it, which is what a flow wants anyway - the
    // message is meant for the one flow its id points at, not for all of them.
    opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
});

// Sagas are loaded from storage rather than built by the container, so they cannot take
// this through a constructor - the handler methods on Flow<> ask for it as a parameter
// instead, and Wolverine resolves it from here on every message.
builder.Services.AddSingleton<DormouseContext>();

using var host = builder.Build();
await host.StartAsync();

var bus = host.Services.GetRequiredService<IMessageBus>();
var sagas = host.Services.GetRequiredService<InMemorySagaPersistor>();
var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Demo");

async Task Send(object message)
{
    try
    {
        await host.SendMessageAndWaitAsync(message);
    }
    catch (Exception e)
    {
        var inner = e.InnerException ?? e;
        log.LogError("!!! {Message} failed: {Type}: {Error}",
            message.GetType().Name, inner.GetType().Name, inner.Message);
    }
}

void ShowState(string id)
{
    var flow = sagas.Load<FlowSaga>(id);
    log.LogInformation("--> FlowSaga state for {Id}: {State}", id,
        flow is null ? "(none)" : $"id={flow.Id}, {flow.FlowState.Count} message(s)");
}

void ShowMessages(string id)
{
    var flow = sagas.Load<FlowSaga>(id);
    if (flow is null) return;

    log.LogInformation("--> state recorded on the flow:");
    foreach (var entry in flow.FlowState)
    {
        var (stateType, effectId, type, payload) = Flow.Decode(entry);
        log.LogInformation("      {Kind}{EffectId} {Type}: {Payload}",
            stateType,
            effectId is null ? "" : $" #{effectId}",
            type.Name,
            Encoding.UTF8.GetString(payload));
    }
}

log.LogInformation("=== FlowSaga alone ===");
await Send(new StartOrder("order-1", "Coffee grinder"));
ShowState("order-1");

await Send(new PaymentReceived("order-1", 249.95m));
ShowState("order-1");

await Send(new OrderShipped("order-1", "TRACK-123"));
ShowState("order-1");

// Nothing cascades this now that the Order saga is gone, so send it directly
await Send(new OrderCompleted("order-1"));
ShowState("order-1");

// A TimeoutMessage is always scheduled, so publish it and wait for delivery
log.LogInformation("Publishing the scheduled OrderTimeout...");
await bus.PublishAsync(new OrderTimeout("order-1"));
await Task.Delay(TimeSpan.FromSeconds(7));
ShowState("order-1");
ShowMessages("order-1");

await host.StopAsync();
