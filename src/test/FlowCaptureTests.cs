namespace Dormouse.Tests;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dormouse.Messages;

// Every test here is given a timeout. A flow that never signals its flush leaves the await in
// StartOrHandle pending rather than failing, and an unfinished Flow does exactly that - without
// a timeout the run does not end, it just stops.
[TestClass]
public sealed class FlowCaptureTests
{
    private static readonly StartOrder Start = new("order-1", "Coffee grinder");

    // Wolverine resolves this from the container and hands it to the handler methods; here
    // there is no container, so the tests pass the instance in themselves. One per test rather
    // than one for the class: the context owns the cache of running flows, so a shared one
    // would hand the next test the flow this one left behind under "order-1".
    private readonly DormouseContext SagaContext = new();

    // Set by MSTest before each test. The timeout is cooperative: when it elapses MSTest cancels
    // this token and then waits for the test to finish, rather than abandoning it - so every
    // await on a flow watches the token, or a hung flow would hang the run just the same.
    public TestContext TestContext { get; set; } = null!;
    private CancellationToken TimeoutToken => TestContext.CancellationToken;

    // Stands in for a reload: storage hands back the recorded state, not the instance that
    // wrote it, so the replaying flow is a fresh object with a copy of FlowState.
    private static TFlow Reload<TFlow>(Flow<StartOrder, PaymentReceived, OrderShipped, OrderTimeout, OrderCompleted> flow)
        where TFlow : Flow<StartOrder, PaymentReceived, OrderShipped, OrderTimeout, OrderCompleted>, new()
        => new() { Id = flow.Id, FlowState = [..flow.FlowState] };

    // Flow decodes its own state privately, so the tests read it back through FlowStateReader.
    private List<FlowStateEntry> Effects(Flow flow) => FlowStateReader.Effects(flow, SagaContext).ToList();
    private List<FlowStateEntry> Messages(Flow flow) => FlowStateReader.Messages(flow, SagaContext).ToList();

    private sealed class TwoEffectFlow : Flow<StartOrder, PaymentReceived, OrderShipped, OrderTimeout, OrderCompleted>
    {
        public int Executions;
        public List<string> Captured = [];

        protected override async Task Run(StartOrder message)
        {
            Captured.Add(await Capture(() => Task.FromResult($"first-{++Executions}")));
            Captured.Add(await Capture(() => Task.FromResult($"second-{++Executions}")));
        }
    }

    [TestMethod, Timeout(5000, CooperativeCancellation = true)]
    public async Task CaptureExecutesTheFuncAndReturnsItsResultOnTheFirstRun()
    {
        var flow = new TwoEffectFlow();

        await flow.StartOrHandle(Start, "order-1", SagaContext).WaitAsync(TimeoutToken);

        Assert.AreEqual(2, flow.Executions);
        CollectionAssert.AreEqual(new[] { "first-1", "second-2" }, flow.Captured);
    }

    [TestMethod, Timeout(5000, CooperativeCancellation = true)]
    public async Task ReplayingAFlowReturnsTheCapturedResultsWithoutRunningTheFuncsAgain()
    {
        var flow = new TwoEffectFlow();
        await flow.StartOrHandle(Start, "order-1", SagaContext).WaitAsync(TimeoutToken);

        var replayed = Reload<TwoEffectFlow>(flow);
        await replayed.StartOrHandle(Start, "order-1", SagaContext).WaitAsync(TimeoutToken);

        // The point of Capture: the side effects did not happen a second time, yet Run saw
        // the same values it saw the first time round.
        Assert.AreEqual(0, replayed.Executions);
        CollectionAssert.AreEqual(new[] { "first-1", "second-2" }, replayed.Captured);
    }

    [TestMethod, Timeout(5000, CooperativeCancellation = true)]
    public async Task ReplayDoesNotRecordTheSameEffectTwice()
    {
        var flow = new TwoEffectFlow();
        await flow.StartOrHandle(Start, "order-1", SagaContext).WaitAsync(TimeoutToken);

        var replayed = Reload<TwoEffectFlow>(flow);
        await replayed.StartOrHandle(Start, "order-1", SagaContext).WaitAsync(TimeoutToken);

        // Two effects before and after, even though Run executed twice - the replay read
        // them rather than appending its own copies.
        Assert.HasCount(2, Effects(flow));
        Assert.HasCount(2, Effects(replayed));
    }

    [TestMethod, Timeout(5000, CooperativeCancellation = true)]
    public async Task EffectsAreRecordedAsEffectEntriesNumberedInCaptureOrder()
    {
        var flow = new TwoEffectFlow();

        await flow.StartOrHandle(Start, "order-1", SagaContext).WaitAsync(TimeoutToken);

        var effects = Effects(flow);
        Assert.HasCount(2, effects);
        Assert.AreEqual(0, effects[0].Index);
        Assert.AreEqual(1, effects[1].Index);
        Assert.AreEqual(typeof(string), effects[0].Type);
        Assert.AreEqual("\"first-1\"", effects[0].Payload.ToStringFromUtf8Bytes());
        Assert.AreEqual("\"second-2\"", effects[1].Payload.ToStringFromUtf8Bytes());
    }

    [TestMethod, Timeout(5000, CooperativeCancellation = true)]
    public async Task HandledMessagesAreRecordedAsMessageEntriesNumberedInArrivalOrder()
    {
        var flow = new TwoEffectFlow();
        await flow.StartOrHandle(Start, "order-1", SagaContext).WaitAsync(TimeoutToken);

        await flow.Handle(new PaymentReceived("order-1", 249.95m), SagaContext).WaitAsync(TimeoutToken);

        var messages = Messages(flow);
        Assert.HasCount(2, messages);
        // The message that started the flow is recorded as the initial one; what arrives after
        // it is an ordinary message entry.
        Assert.AreEqual(StateType.InitialMessage, messages[0].StateType);
        Assert.AreEqual(typeof(StartOrder), messages[0].Type);
        Assert.AreEqual(StateType.Message, messages[1].StateType);
        Assert.AreEqual(typeof(PaymentReceived), messages[1].Type);
        // Numbered 0 and 1 even though two effect entries sit between them: messages are counted
        // on their own sequence, so the captures in between do not push the indexes along.
        CollectionAssert.AreEqual(new[] { 0, 1 }, messages.Select(e => e.Index).ToArray());
        Assert.AreEqual("""{"OrderId":"order-1","Amount":249.95}""", messages[1].Payload.ToStringFromUtf8Bytes());
    }

    private sealed class FailingSecondEffectFlow : Flow<StartOrder, PaymentReceived, OrderShipped, OrderTimeout, OrderCompleted>
    {
        public int FirstExecutions;
        public int SecondExecutions;
        public bool ThrowOnSecond = true;
        public string? Second;

        protected override async Task Run(StartOrder message)
        {
            await Capture(() => Task.FromResult($"first-{++FirstExecutions}"));
            Second = await Capture(() =>
            {
                SecondExecutions++;
                return ThrowOnSecond ? throw new InvalidOperationException("boom") : Task.FromResult("second");
            });
        }
    }

    [TestMethod, Timeout(5000, CooperativeCancellation = true)]
    public async Task AnEffectThatThrowsIsNotRecordedAndIsRetriedOnTheNextRun()
    {
        var flow = new FailingSecondEffectFlow();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => flow.StartOrHandle(Start, "order-1", SagaContext).WaitAsync(TimeoutToken));

        // Only the effect that completed made it into the state.
        Assert.HasCount(1, Effects(flow));

        var retried = Reload<FailingSecondEffectFlow>(flow);
        retried.ThrowOnSecond = false;
        await retried.StartOrHandle(Start, "order-1", SagaContext).WaitAsync(TimeoutToken);

        // The first effect replayed, the failed one ran again - and picked up the id it had
        // already been given, rather than shifting to a new one.
        Assert.AreEqual(0, retried.FirstExecutions);
        Assert.AreEqual(1, retried.SecondExecutions);
        Assert.AreEqual("second", retried.Second);
        CollectionAssert.AreEqual(new[] { 0, 1 }, Effects(retried).Select(e => e.Index).ToArray());
    }

    [TestMethod, Timeout(5000, CooperativeCancellation = true)]
    public async Task ACapturedMessageWakesTheFlowWithoutRecordingAMessageOfItsOwn()
    {
        var flow = new FailingSecondEffectFlow();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => flow.StartOrHandle(Start, "order-1", SagaContext).WaitAsync(TimeoutToken));

        // Captured says an effect completed somewhere else, and nothing more than that. It drives
        // the flow on to the end, but it is not a message the flow was sent - so the only message
        // on record afterwards is still the one that started it.
        var reloaded = Reload<FailingSecondEffectFlow>(flow);
        reloaded.ThrowOnSecond = false;

        await reloaded.Handle(new Captured("order-1"), SagaContext).WaitAsync(TimeoutToken);

        Assert.AreEqual("second", reloaded.Second);
        Assert.HasCount(1, Messages(reloaded));
    }

    private sealed class RecordingFlow : Flow<StartOrder, PaymentReceived, OrderShipped, OrderTimeout, OrderCompleted>
    {
        public List<StartOrder> RunWith = [];
        public int Executions;
        public string? Captured;

        protected override async Task Run(StartOrder message)
        {
            RunWith.Add(message);
            Captured = await Capture(() => Task.FromResult($"effect-{++Executions}"));
        }
    }

    [TestMethod, Timeout(5000, CooperativeCancellation = true)]
    public async Task RunIsGivenTheMessageThatStartedTheFlowRatherThanTheOneBeingHandled()
    {
        var flow = new RecordingFlow();

        await flow.StartOrHandle(Start, "order-1", SagaContext).WaitAsync(TimeoutToken);
        await flow.Handle(new PaymentReceived("order-1", 249.95m), SagaContext).WaitAsync(TimeoutToken);
        await flow.Handle(new OrderShipped("order-1", "TRACK-123"), SagaContext).WaitAsync(TimeoutToken);

        // Run is written against the start message and started once; the messages that follow
        // are handed to it through its own state, never as the argument it runs on.
        Assert.HasCount(1, flow.RunWith);
        Assert.AreEqual(Start, flow.RunWith.Single());
        CollectionAssert.AreEqual(
            new[] { typeof(StartOrder), typeof(PaymentReceived), typeof(OrderShipped) },
            Messages(flow).Select(e => e.Type).ToArray());
    }

    [TestMethod, Timeout(5000, CooperativeCancellation = true)]
    public async Task LaterMessagesDoNotReExecuteEffectsRecordedByAnEarlierOne()
    {
        var flow = new RecordingFlow();

        await flow.StartOrHandle(Start, "order-1", SagaContext).WaitAsync(TimeoutToken);
        await flow.Handle(new PaymentReceived("order-1", 249.95m), SagaContext).WaitAsync(TimeoutToken);
        await flow.Handle(new OrderShipped("order-1", "TRACK-123"), SagaContext).WaitAsync(TimeoutToken);

        Assert.AreEqual(1, flow.Executions);
        Assert.AreEqual("effect-1", flow.Captured);
        Assert.HasCount(1, Effects(flow));
    }

    [TestMethod, Timeout(5000, CooperativeCancellation = true)]
    public async Task TheInitialMessageIsReadBackOutOfStateRatherThanHeldOnTo()
    {
        var flow = new RecordingFlow();
        await flow.StartOrHandle(Start, "order-1", SagaContext).WaitAsync(TimeoutToken);

        // Nothing survives between messages except FlowState, so a reloaded flow has only
        // the recorded copy to hand Run - equal to the original, but not the same instance.
        var reloaded = Reload<RecordingFlow>(flow);
        await reloaded.Handle(new OrderCompleted("order-1"), SagaContext).WaitAsync(TimeoutToken);

        var replayedWith = reloaded.RunWith.Single();
        Assert.AreEqual(Start, replayedWith);
        Assert.AreNotSame(Start, replayedWith);
        Assert.AreEqual(0, reloaded.Executions);
        Assert.AreEqual("effect-1", reloaded.Captured);
    }

    [TestMethod, Timeout(5000, CooperativeCancellation = true)]
    public async Task AFlowWhoseStateDoesNotStartWithTheStartMessageRefusesToRun()
    {
        // Reached only if a "Handle" message is delivered to a flow that was never started,
        // which would otherwise replay Run against a message it was not written for. The id is
        // set because Wolverine works it out from the message before the handler is called -
        // what this flow is missing is recorded state, not an identity.
        var flow = new RecordingFlow { Id = "order-1" };

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => flow.Handle(new PaymentReceived("order-1", 249.95m), SagaContext).WaitAsync(TimeoutToken));

        StringAssert.Contains(error.Message, nameof(StartOrder));
        Assert.IsEmpty(flow.RunWith);
    }

    private sealed class ContextRecordingFlow : Flow<StartOrder, PaymentReceived, OrderShipped, OrderTimeout, OrderCompleted>
    {
        public List<DormouseContext> SeenBy = [];

        protected override Task Run(StartOrder message)
        {
            SeenBy.Add(Context);
            return Task.CompletedTask;
        }
    }

    [TestMethod, Timeout(5000, CooperativeCancellation = true)]
    public async Task TheContextHandedToAHandlerIsWhatRunSees()
    {
        var flow = new ContextRecordingFlow();

        await flow.StartOrHandle(Start, "order-1", SagaContext).WaitAsync(TimeoutToken);
        await flow.Handle(new PaymentReceived("order-1", 249.95m), SagaContext).WaitAsync(TimeoutToken);
        await flow.Handle(new Captured("order-1"), SagaContext).WaitAsync(TimeoutToken);

        // Whatever the container resolved for the message that started the flow is what Run
        // runs against - the same instance every time here, since it is registered as a
        // singleton, and every later message sets it again before waking the flow.
        Assert.IsNotEmpty(flow.SeenBy);
        Assert.IsTrue(flow.SeenBy.TrueForAll(c => ReferenceEquals(c, SagaContext)));
    }

    private sealed class ContextOutsideAHandlerFlow : Flow<StartOrder, PaymentReceived, OrderShipped, OrderTimeout, OrderCompleted>
    {
        // Run is protected, as it is on every flow - this is only here so the test can reach it
        // without a message having been handled first.
        public Task RunDirectly(StartOrder message) => Run(message);

        protected override Task Run(StartOrder message)
        {
            _ = Context;
            return Task.CompletedTask;
        }
    }

    [TestMethod, Timeout(5000, CooperativeCancellation = true)]
    public async Task ReadingTheContextOutsideOfAHandlerFails()
    {
        // Nothing set it, so it is not there to be read: the flow only has a context while a
        // message is being handled, and silently handing back null would hide that.
        var flow = new ContextOutsideAHandlerFlow();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => flow.RunDirectly(Start));
    }

    private sealed class MixedResultFlow : Flow<StartOrder, PaymentReceived, OrderShipped, OrderTimeout, OrderCompleted>
    {
        public int Executions;
        public int Number;
        public string? Nothing;
        public OrderShipped? Record;

        protected override async Task Run(StartOrder message)
        {
            Number = await Capture(() => { Executions++; return Task.FromResult(42); });
            Nothing = await Capture(() => { Executions++; return Task.FromResult<string?>(null); });
            Record = await Capture(() => { Executions++; return Task.FromResult(new OrderShipped("order-1", "TRACK-123")); });
        }
    }

    [TestMethod, Timeout(5000, CooperativeCancellation = true)]
    public async Task ValueTypeNullAndRecordResultsAllSurviveAReplay()
    {
        var flow = new MixedResultFlow();
        await flow.StartOrHandle(Start, "order-1", SagaContext).WaitAsync(TimeoutToken);

        var replayed = Reload<MixedResultFlow>(flow);
        await replayed.StartOrHandle(Start, "order-1", SagaContext).WaitAsync(TimeoutToken);

        Assert.AreEqual(0, replayed.Executions);
        Assert.AreEqual(42, replayed.Number);
        // A null result is a captured value in its own right, not a missing effect - if it
        // were treated as absent the func would run again on every replay.
        Assert.IsNull(replayed.Nothing);
        Assert.AreEqual(new OrderShipped("order-1", "TRACK-123"), replayed.Record);
    }

    [TestMethod, Timeout(5000, CooperativeCancellation = true)]
    public async Task MessageIndexesCarryOnAcrossAReloadRatherThanRestarting()
    {
        var flow = new TwoEffectFlow();
        await flow.StartOrHandle(Start, "order-1", SagaContext).WaitAsync(TimeoutToken);
        await flow.Handle(new PaymentReceived("order-1", 249.95m), SagaContext).WaitAsync(TimeoutToken);

        // A reloaded flow has nothing in memory, so the index for the next message can only
        // come from the state it folds - if it did not, this third message would collide with
        // the first and overwrite it in the inbox.
        var reloaded = Reload<TwoEffectFlow>(flow);
        await reloaded.Handle(new OrderShipped("order-1", "TRACK-123"), SagaContext).WaitAsync(TimeoutToken);

        var messages = Messages(reloaded);
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, messages.Select(e => e.Index).ToArray());
        CollectionAssert.AreEqual(
            new[] { typeof(StartOrder), typeof(PaymentReceived), typeof(OrderShipped) },
            messages.Select(e => e.Type).ToArray());
    }
}
