namespace Dormouse.Tests;

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dormouse.Messages;

// Flow<T1> is the whole of a flow that is only ever started - no follow-up messages, so
// StartOrHandle and the Captured handler are all there is to it.
[TestClass]
public sealed class FlowSingleMessageTests
{
    private static readonly StartOrder Start = new("order-1", "Coffee grinder");
    private static readonly DormouseContext SagaContext = new();

    private sealed class SingleMessageFlow : Flow<StartOrder>
    {
        public List<StartOrder> RunWith = [];
        public int Executions;
        public string? Captured;
        public DormouseContext? SeenContext;

        public override async Task Run(StartOrder message)
        {
            RunWith.Add(message);
            SeenContext = Context;
            Captured = await Capture(() => Task.FromResult($"effect-{++Executions}"));
        }
    }

    private static SingleMessageFlow Reload(SingleMessageFlow flow)
        => new() { FlowState = [..flow.FlowState] };

    private static IEnumerable<FlowStateEntry> Effects(SingleMessageFlow flow)
        => flow.FlowState.Select(Flow.Decode).Where(e => e.StateType == StateType.Effect);

    [TestMethod]
    public async Task StartingASingleMessageFlowRunsItAndRecordsWhatItCaptured()
    {
        var flow = new SingleMessageFlow();

        await flow.StartOrHandle(Start, "order-1", SagaContext);

        Assert.AreEqual("order-1", flow.Id);
        CollectionAssert.AreEqual(new[] { Start }, flow.RunWith);
        Assert.AreEqual("effect-1", flow.Captured);
        Assert.AreSame(SagaContext, flow.SeenContext);
        Assert.HasCount(1, Effects(flow).ToList());
    }

    [TestMethod]
    public async Task ReplayingASingleMessageFlowReadsItsEffectsBackInsteadOfRunningThem()
    {
        var flow = new SingleMessageFlow();
        await flow.StartOrHandle(Start, "order-1", SagaContext);

        var replayed = Reload(flow);
        await replayed.StartOrHandle(Start, "order-1", SagaContext);

        Assert.AreEqual(0, replayed.Executions);
        Assert.AreEqual("effect-1", replayed.Captured);
        Assert.HasCount(1, Effects(replayed).ToList());
    }

    [TestMethod]
    public async Task ACapturedMessageIsTheOneThingASingleMessageFlowCanBeHandedAfterStarting()
    {
        // Nothing else arrives for this flow, so an effect completing outside of Run is what
        // moves it along: the result comes in as a message and the replay reads it.
        var flow = new SingleMessageFlow();
        await flow.StartOrHandle(Start, "order-1", SagaContext);

        var reloaded = Reload(flow);
        await reloaded.Handle(Captured.Create(1, "from-outside"), SagaContext);

        Assert.AreEqual(0, reloaded.Executions);
        Assert.AreEqual("effect-1", reloaded.Captured);
        CollectionAssert.AreEqual(new int?[] { 0, 1 }, Effects(reloaded).Select(e => e.EffectId).ToArray());
        Assert.AreSame(SagaContext, reloaded.SeenContext);
    }

    [TestMethod]
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
