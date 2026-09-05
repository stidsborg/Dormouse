namespace Dormouse.Tests;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dormouse.Messages;

// Flow<T1> is the whole of a flow that is only ever started - no follow-up messages, so
// StartOrHandle and the Captured handler are all there is to it.
//
// Timeouts for the same reason as in FlowCaptureTests: a flow that never flushes leaves the
// await pending instead of failing.
[TestClass]
public sealed class FlowSingleMessageTests
{
    private static readonly StartOrder Start = new("order-1", "Coffee grinder");
    private readonly DormouseContext SagaContext = new();

    // Cooperative timeout, as in FlowCaptureTests: every await on the flow watches this token.
    public TestContext TestContext { get; set; } = null!;
    private CancellationToken TimeoutToken => TestContext.CancellationToken;

    private sealed class SingleMessageFlow : Flow<StartOrder>
    {
        public List<StartOrder> RunWith = [];
        public int Executions;
        public string? Captured;
        public DormouseContext? SeenContext;

        protected override async Task Run(StartOrder message)
        {
            RunWith.Add(message);
            SeenContext = Context;
            Captured = await Capture(() => Task.FromResult($"effect-{++Executions}"));
        }
    }

    private static SingleMessageFlow Reload(SingleMessageFlow flow)
        => new() { Id = flow.Id, FlowState = [..flow.FlowState] };

    // Flow decodes its own state privately, so the tests read it back through FlowStateReader.
    private List<FlowStateEntry> Effects(SingleMessageFlow flow) => FlowStateReader.Effects(flow, SagaContext).ToList();

    [TestMethod, Timeout(5000, CooperativeCancellation = true)]
    public async Task StartingASingleMessageFlowRunsItAndRecordsWhatItCaptured()
    {
        var flow = new SingleMessageFlow();

        await flow.StartOrHandle(Start, "order-1", SagaContext).WaitAsync(TimeoutToken);

        Assert.AreEqual("order-1", flow.Id);
        CollectionAssert.AreEqual(new[] { Start }, flow.RunWith);
        Assert.AreEqual("effect-1", flow.Captured);
        Assert.AreSame(SagaContext, flow.SeenContext);
        Assert.HasCount(1, Effects(flow));
    }

    [TestMethod, Timeout(5000, CooperativeCancellation = true)]
    public async Task ReplayingASingleMessageFlowReadsItsEffectsBackInsteadOfRunningThem()
    {
        var flow = new SingleMessageFlow();
        await flow.StartOrHandle(Start, "order-1", SagaContext).WaitAsync(TimeoutToken);

        var replayed = Reload(flow);
        await replayed.StartOrHandle(Start, "order-1", SagaContext).WaitAsync(TimeoutToken);

        Assert.AreEqual(0, replayed.Executions);
        Assert.AreEqual("effect-1", replayed.Captured);
        Assert.HasCount(1, Effects(replayed));
    }

    [TestMethod, Timeout(5000, CooperativeCancellation = true)]
    public async Task ACapturedMessageIsTheOneThingASingleMessageFlowCanBeHandedAfterStarting()
    {
        // Nothing else arrives for this flow, so an effect completing outside of Run is what
        // moves it along. The message says only that one did - no result rides along on it, so
        // the flow picks that up out of its own recorded state.
        var flow = new SingleMessageFlow();
        await flow.StartOrHandle(Start, "order-1", SagaContext).WaitAsync(TimeoutToken);

        var reloaded = Reload(flow);
        await reloaded.Handle(new Captured("order-1"), SagaContext).WaitAsync(TimeoutToken);

        Assert.AreEqual(0, reloaded.Executions);
        Assert.AreEqual("effect-1", reloaded.Captured);
        Assert.HasCount(1, Effects(reloaded));
        Assert.AreSame(SagaContext, reloaded.SeenContext);
    }

    [TestMethod, Timeout(5000, CooperativeCancellation = true)]
    public void BothFlowVariantsAreTheSameFlowUnderneath()
    {
        // Siblings, not one deriving from the other: what they share is the non-generic Flow
        // they both inherit - the state, the replay, and Capture - so what is tested here
        // holds for the five-message flow too.
        Assert.IsInstanceOfType<Flow>(new SingleMessageFlow());
        Assert.IsInstanceOfType<Flow>(new FlowSaga());
        Assert.IsNotInstanceOfType<Flow<StartOrder>>(new FlowSaga());
    }
}
