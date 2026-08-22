namespace Dormouse.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dormouse.Messages;

[TestClass]
public sealed class FlowCaptureTests
{
    private static readonly StartOrder Start = new("order-1", "Coffee grinder");

    // Wolverine resolves this from the container and hands it to the handler methods; here
    // there is no container, so the tests pass the instance in themselves.
    private static readonly DormouseContext SagaContext = new();

    // Stands in for a reload: storage hands back the recorded state, not the instance that
    // wrote it, so the replaying flow is a fresh object with a copy of FlowState.
    private static TFlow Reload<TFlow>(Flow<StartOrder, PaymentReceived, OrderShipped, OrderTimeout, OrderCompleted> flow)
        where TFlow : Flow<StartOrder, PaymentReceived, OrderShipped, OrderTimeout, OrderCompleted>, new()
        => new() { FlowState = [..flow.FlowState] };

    private static IEnumerable<FlowStateEntry> Effects(
        Flow<StartOrder, PaymentReceived, OrderShipped, OrderTimeout, OrderCompleted> flow)
        => flow.FlowState.Select(Flow.Decode).Where(e => e.StateType == StateType.Effect);

    private sealed class TwoEffectFlow : Flow<StartOrder, PaymentReceived, OrderShipped, OrderTimeout, OrderCompleted>
    {
        public int Executions;
        public List<string> Captured = [];

        public override async Task Run(StartOrder message)
        {
            Captured.Add(await Capture(() => Task.FromResult($"first-{++Executions}")));
            Captured.Add(await Capture(() => Task.FromResult($"second-{++Executions}")));
        }
    }

    [TestMethod]
    public async Task CaptureExecutesTheFuncAndReturnsItsResultOnTheFirstRun()
    {
        var flow = new TwoEffectFlow();

        await flow.StartOrHandle(Start, "order-1", SagaContext);

        Assert.AreEqual(2, flow.Executions);
        CollectionAssert.AreEqual(new[] { "first-1", "second-2" }, flow.Captured);
    }

    [TestMethod]
    public async Task ReplayingAFlowReturnsTheCapturedResultsWithoutRunningTheFuncsAgain()
    {
        var flow = new TwoEffectFlow();
        await flow.StartOrHandle(Start, "order-1", SagaContext);

        var replayed = Reload<TwoEffectFlow>(flow);
        await replayed.StartOrHandle(Start, "order-1", SagaContext);

        // The point of Capture: the side effects did not happen a second time, yet Run saw
        // the same values it saw the first time round.
        Assert.AreEqual(0, replayed.Executions);
        CollectionAssert.AreEqual(new[] { "first-1", "second-2" }, replayed.Captured);
    }

    [TestMethod]
    public async Task ReplayDoesNotRecordTheSameEffectTwice()
    {
        var flow = new TwoEffectFlow();
        await flow.StartOrHandle(Start, "order-1", SagaContext);

        var replayed = Reload<TwoEffectFlow>(flow);
        await replayed.StartOrHandle(Start, "order-1", SagaContext);

        // Two effects before and after, even though Run executed twice - the replay read
        // them rather than appending its own copies.
        Assert.HasCount(2, Effects(flow).ToList());
        Assert.HasCount(2, Effects(replayed).ToList());
    }

    [TestMethod]
    public async Task EffectsAreRecordedAsEffectEntriesNumberedInCaptureOrder()
    {
        var flow = new TwoEffectFlow();

        await flow.StartOrHandle(Start, "order-1", SagaContext);

        var effects = Effects(flow).ToList();
        Assert.HasCount(2, effects);
        Assert.AreEqual(0, effects[0].EffectId);
        Assert.AreEqual(1, effects[1].EffectId);
        Assert.AreEqual(typeof(string), effects[0].Type);
        Assert.AreEqual("\"first-1\"", effects[0].Payload.ToStringFromUtf8Bytes());
        Assert.AreEqual("\"second-2\"", effects[1].Payload.ToStringFromUtf8Bytes());
    }

    [TestMethod]
    public async Task HandledMessagesAreRecordedAsMessageEntriesWithNoEffectId()
    {
        var flow = new TwoEffectFlow();
        await flow.StartOrHandle(Start, "order-1", SagaContext);

        await flow.Handle(new PaymentReceived("order-1", 249.95m), SagaContext);

        var messages = flow.FlowState.Select(Flow.Decode).Where(e => e.StateType == StateType.Message).ToList();
        Assert.HasCount(2, messages);
        Assert.AreEqual(typeof(StartOrder), messages[0].Type);
        Assert.IsNull(messages[0].EffectId);
        Assert.AreEqual(typeof(PaymentReceived), messages[1].Type);
        Assert.AreEqual("""{"OrderId":"order-1","Amount":249.95}""", messages[1].Payload.ToStringFromUtf8Bytes());
    }

    private sealed class FailingSecondEffectFlow : Flow<StartOrder, PaymentReceived, OrderShipped, OrderTimeout, OrderCompleted>
    {
        public int FirstExecutions;
        public int SecondExecutions;
        public bool ThrowOnSecond = true;
        public string? Second;

        public override async Task Run(StartOrder message)
        {
            await Capture(() => Task.FromResult($"first-{++FirstExecutions}"));
            Second = await Capture(() =>
            {
                SecondExecutions++;
                return ThrowOnSecond ? throw new InvalidOperationException("boom") : Task.FromResult("second");
            });
        }
    }

    [TestMethod]
    public async Task AnEffectThatThrowsIsNotRecordedAndIsRetriedOnTheNextRun()
    {
        var flow = new FailingSecondEffectFlow();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => flow.StartOrHandle(Start, "order-1", SagaContext));

        // Only the effect that completed made it into the state.
        Assert.HasCount(1, Effects(flow).ToList());

        var retried = Reload<FailingSecondEffectFlow>(flow);
        retried.ThrowOnSecond = false;
        await retried.StartOrHandle(Start, "order-1", SagaContext);

        // The first effect replayed, the failed one ran again - and picked up the id it had
        // already been given, rather than shifting to a new one.
        Assert.AreEqual(0, retried.FirstExecutions);
        Assert.AreEqual(1, retried.SecondExecutions);
        Assert.AreEqual("second", retried.Second);
        CollectionAssert.AreEqual(new int?[] { 0, 1 }, Effects(retried).Select(e => e.EffectId).ToArray());
    }

    [TestMethod]
    public async Task ACapturedMessageSuppliesAnEffectResultInsteadOfTheFuncRunning()
    {
        var flow = new FailingSecondEffectFlow();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => flow.StartOrHandle(Start, "order-1", SagaContext));

        // The second effect never completed, so its result arrives from outside instead. The
        // func is still set to throw: if the replay ran it rather than reading the message,
        // this would not get past the Handle call.
        var reloaded = Reload<FailingSecondEffectFlow>(flow);
        await reloaded.Handle(Captured.Create(1, "from-outside"), SagaContext);

        Assert.AreEqual(0, reloaded.SecondExecutions);
        Assert.AreEqual("from-outside", reloaded.Second);
        CollectionAssert.AreEqual(new int?[] { 0, 1 }, Effects(reloaded).Select(e => e.EffectId).ToArray());
    }

    [TestMethod]
    public async Task ACapturedMessageIsRecordedUnderTheTypeItWasSentAs()
    {
        var flow = new FailingSecondEffectFlow();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => flow.StartOrHandle(Start, "order-1", SagaContext));

        await flow.Handle(Captured.Create(1, "from-outside"), SagaContext);

        // The entry it became is the one Capture would have written, type and all - a replay
        // cannot tell the two apart.
        var supplied = Effects(flow).Single(e => e.EffectId == 1);
        Assert.AreEqual(typeof(string), supplied.Type);
        Assert.AreEqual("\"from-outside\"", supplied.Payload.ToStringFromUtf8Bytes());
    }

    [TestMethod]
    public void CreateRoundTripsThePayloadAndTheTypeItWasSerializedAs()
    {
        var (type, payload) = Captured.Create(3, new OrderShipped("order-1", "TRACK-123")).Decode();

        Assert.AreEqual(typeof(OrderShipped), type);
        Assert.AreEqual(
            new OrderShipped("order-1", "TRACK-123"),
            JsonSerializer.Deserialize<OrderShipped>(payload));
    }

    private sealed class RecordingFlow : Flow<StartOrder, PaymentReceived, OrderShipped, OrderTimeout, OrderCompleted>
    {
        public List<StartOrder> RunWith = [];
        public int Executions;
        public string? Captured;

        public override async Task Run(StartOrder message)
        {
            RunWith.Add(message);
            Captured = await Capture(() => Task.FromResult($"effect-{++Executions}"));
        }
    }

    [TestMethod]
    public async Task EveryMessageReplaysRunWithTheMessageThatStartedTheFlow()
    {
        var flow = new RecordingFlow();

        await flow.StartOrHandle(Start, "order-1", SagaContext);
        await flow.Handle(new PaymentReceived("order-1", 249.95m), SagaContext);
        await flow.Handle(new OrderShipped("order-1", "TRACK-123"), SagaContext);

        // Run saw the StartOrder all three times - never the message being handled.
        Assert.HasCount(3, flow.RunWith);
        CollectionAssert.AreEqual(new[] { Start, Start, Start }, flow.RunWith);
    }

    [TestMethod]
    public async Task ReplayingRunDoesNotReExecuteEffectsRecordedByAnEarlierMessage()
    {
        var flow = new RecordingFlow();

        await flow.StartOrHandle(Start, "order-1", SagaContext);
        await flow.Handle(new PaymentReceived("order-1", 249.95m), SagaContext);
        await flow.Handle(new OrderShipped("order-1", "TRACK-123"), SagaContext);

        Assert.AreEqual(1, flow.Executions);
        Assert.AreEqual("effect-1", flow.Captured);
        Assert.HasCount(1, Effects(flow).ToList());
    }

    [TestMethod]
    public async Task TheInitialMessageIsReadBackOutOfStateRatherThanHeldOnTo()
    {
        var flow = new RecordingFlow();
        await flow.StartOrHandle(Start, "order-1", SagaContext);

        // Nothing survives between messages except FlowState, so a reloaded flow has only
        // the recorded copy to hand Run - equal to the original, but not the same instance.
        var reloaded = Reload<RecordingFlow>(flow);
        await reloaded.Handle(new OrderCompleted("order-1"), SagaContext);

        var replayedWith = reloaded.RunWith.Single();
        Assert.AreEqual(Start, replayedWith);
        Assert.AreNotSame(Start, replayedWith);
        Assert.AreEqual(0, reloaded.Executions);
        Assert.AreEqual("effect-1", reloaded.Captured);
    }

    [TestMethod]
    public async Task AFlowWhoseStateDoesNotStartWithTheStartMessageRefusesToRun()
    {
        // Reached only if a "Handle" message is delivered to a flow that was never started,
        // which would otherwise replay Run against a message it was not written for.
        var flow = new RecordingFlow();

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => flow.Handle(new PaymentReceived("order-1", 249.95m), SagaContext));

        StringAssert.Contains(error.Message, nameof(StartOrder));
        Assert.IsEmpty(flow.RunWith);
    }

    private sealed class ContextRecordingFlow : Flow<StartOrder, PaymentReceived, OrderShipped, OrderTimeout, OrderCompleted>
    {
        public List<DormouseContext> SeenBy = [];

        public override Task Run(StartOrder message)
        {
            SeenBy.Add(Context);
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    public async Task TheContextHandedToAHandlerIsWhatRunSees()
    {
        var flow = new ContextRecordingFlow();

        await flow.StartOrHandle(Start, "order-1", SagaContext);
        await flow.Handle(new PaymentReceived("order-1", 249.95m), SagaContext);
        await flow.Handle(Captured.Create(0, "from-outside"), SagaContext);

        // Whatever the container resolved for this message is what the replay runs against -
        // the same instance every time here, since it is registered as a singleton.
        Assert.HasCount(3, flow.SeenBy);
        Assert.IsTrue(flow.SeenBy.TrueForAll(c => ReferenceEquals(c, SagaContext)));
    }

    [TestMethod]
    public void ReadingTheContextOutsideOfAHandlerFails()
    {
        // Nothing set it, so it is not there to be read: the flow only has a context while a
        // message is being handled, and silently handing back null would hide that.
        var flow = new ContextRecordingFlow();

        Assert.ThrowsExactly<InvalidOperationException>(() => flow.Run(Start));
    }

    private sealed class MixedResultFlow : Flow<StartOrder, PaymentReceived, OrderShipped, OrderTimeout, OrderCompleted>
    {
        public int Executions;
        public int Number;
        public string? Nothing;
        public OrderShipped? Record;

        public override async Task Run(StartOrder message)
        {
            Number = await Capture(() => { Executions++; return Task.FromResult(42); });
            Nothing = await Capture(() => { Executions++; return Task.FromResult<string?>(null); });
            Record = await Capture(() => { Executions++; return Task.FromResult(new OrderShipped("order-1", "TRACK-123")); });
        }
    }

    [TestMethod]
    public async Task ValueTypeNullAndRecordResultsAllSurviveAReplay()
    {
        var flow = new MixedResultFlow();
        await flow.StartOrHandle(Start, "order-1", SagaContext);

        var replayed = Reload<MixedResultFlow>(flow);
        await replayed.StartOrHandle(Start, "order-1", SagaContext);

        Assert.AreEqual(0, replayed.Executions);
        Assert.AreEqual(42, replayed.Number);
        // A null result is a captured value in its own right, not a missing effect - if it
        // were treated as absent the func would run again on every replay.
        Assert.IsNull(replayed.Nothing);
        Assert.AreEqual(new OrderShipped("order-1", "TRACK-123"), replayed.Record);
    }
}
