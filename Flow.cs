using System.Buffers.Binary;
using System.Text.Json;
using Wolverine;

namespace Dormouse;

// Everything a flow is made of that says nothing about which messages it is written against:
// the identity, the recorded state, the replay, and Capture on top of it. The generic types
// below add the typed way in and nothing else.
public abstract class Flow : Saga
{
    // Wolverine's saga storage requires a non-null identity, and it does not assign
    // one when it creates the saga - so the flow has to pick it up itself.
    public string? Id { get; set; }
    private string? SagaId { get; set; }

    // Part of the saga state, so it is stored and reloaded along with the flow: every
    // handler invocation appends its message here, and every captured effect its result.
    // Kept as bytes rather than strings so the document store base64s them instead of
    // escaping every quote.
    public List<byte[]> FlowState { get; set; } = [];

    // Rebuilt from FlowState by Initialize rather than persisted in their own right - the
    // flow instance is recreated per message, so whatever is not in FlowState is gone.
    // Effect results stay serialized in here and are only decoded when Capture asks for
    // one, so an effect on a branch that this replay does not reach is never deserialized.
    private readonly Dictionary<int, byte[]> _effects = new();
    private readonly List<object> _inbox = new();
    private int _atEffectId;

    // The message that started the flow, as Initialize read it back: untyped here, because
    // which type it has to be is the one thing this class does not know. Flow<T1> is what
    // says, and what refuses the flow if the state does not agree.
    protected object? RecordedInitialMessage { get; private set; }

    // Not state either, and not something the flow can construct itself: the saga is
    // rehydrated from storage rather than built by the container, so a service has no way
    // in through a constructor. It comes in as a parameter on the handler methods -
    // Wolverine resolves it per message - and is parked here for Run to reach.
    private DormouseContext? _context;

    protected DormouseContext Context
        => _context ?? throw new InvalidOperationException(
            $"Flow '{Id}' has no {nameof(DormouseContext)}: it is only set while a message is being handled");

    public void Deliver(DormouseContext context)
    {
        //todo context.FlowsCache.GetOrSet()
            
    }

    private void Initialize()
    {
        _effects.Clear();
        _inbox.Clear();
        _atEffectId = 0;
        RecordedInitialMessage = null;
        SagaId = Id + "@" + GetType().SimpleQualifiedName();
        
        foreach (var entry in FlowState)
        {
            var (stateType, effectId, type, payload) = Decode(entry);
            if (stateType == StateType.Effect)
            {
                _effects[effectId!.Value] = payload;
                continue;
            }

            var message = JsonSerializer.Deserialize(payload, type)!;
            _inbox.Add(message);
            // Messages are recorded in arrival order, so the first one is what started the flow
            RecordedInitialMessage ??= message;
        }
    }

    // An effect that completed outside of Run: instead of a func returning the value, the value
    // arrives as a message. It is filed under the id the capture was given, so the replay that
    // follows finds it where Capture looks - which is what lets Run carry on past the point it
    // stopped at last time, without running that effect itself.
    //
    // The message carries the type along with the payload, so the entry it becomes is
    // indistinguishable from one Capture recorded itself.
    //
    // Not tied to any of the flow's own message types, so it is declared here rather than
    // alongside them - every flow can be handed one of these, whatever it is written against.
    public Task Handle(Captured msg, DormouseContext context)
    {
        _context = context;

        var (type, payload) = msg.Decode();
        FlowState.Add(Encode(StateType.Effect, msg.Id, type, payload));

        return Replay();
    }

    // Every message replays Run from the top. Record appends the message that just arrived,
    // and Replay does the rest.
    protected Task WriteAndRun<T>(T msg, DormouseContext context)
    {
        _context = context;
        Record(msg);
        return Replay();
    }

    // Initialize reads the whole of the state back in, and the effects it finds there are what
    // keep the replay from redoing work that has already happened.
    //
    // Run is written against the message that began the flow, not the one being handled, so
    // it is handed the message Initialize recovered - the same value on every replay.
    protected Task Replay()
    {
        Initialize();
        return RunFromInitialMessage();
    }

    // The one step of the replay that needs to know the message type: the subclass has the
    // typed Run, and the message to hand it is whatever Initialize recovered.
    protected abstract Task RunFromInitialMessage();

    // Generic rather than taking object: serializing through "object" would use the
    // declared type and write an empty "{}" instead of the message's own properties.
    // todo add idempotency?
    private void Record<T>(T msg)
        => FlowState.Add(Encode(StateType.Message, effectId: null, typeof(T), JsonSerializer.SerializeToUtf8Bytes(msg)));

    public Task<T> Message<T>()
    {
        return Task.FromResult(default(T)!);
    }

    public Task<T> Capture<T>(Func<T> func) => Capture(() => Task.FromResult(func()));
    
    // Runs the func once per flow and remembers what it returned, so replaying Run does not
    // repeat work that has already happened - charging a card, sending a mail, drawing a
    // random number. Effects are identified by the order in which they are captured, which
    // is what makes Run's control flow have to be deterministic across replays.
    public async Task<T> Capture<T>(Func<Task<T>> func)
    {
        var effectId = _atEffectId++;

        if (_effects.TryGetValue(effectId, out var stored))
            return JsonSerializer.Deserialize<T>(stored)!;

        var result = await func();

        // The live result is what gets returned, not a JSON round-trip of it: only later
        // replays - which have no choice - see the deserialized copy.
        var payload = JsonSerializer.SerializeToUtf8Bytes(result);
        _effects[effectId] = payload;
        FlowState.Add(Encode(StateType.Effect, effectId, typeof(T), payload));

        return result;
    }

    public static byte[] Encode(StateType stateType, int? effectId, Type type, byte[] payload)
    {
        byte[]? id = null;
        if (effectId is not null)
            BinaryPrimitives.WriteInt32LittleEndian(id = new byte[4], effectId.Value);

        return ByteArrayMarshaller.Serialize([(byte)stateType], id, type.SerializeType(), payload);
    }

    public static FlowStateEntry Decode(byte[] entry)
    {
        var fields = ByteArrayMarshaller.Deserialize(entry, expectedCount: 4);

        return new FlowStateEntry(
            (StateType)fields[0]!.Value.Span[0],
            fields[1] is { } id ? BinaryPrimitives.ReadInt32LittleEndian(id.Span) : null,
            fields[2]!.Value.ToArray().ResolveType()!,
            fields[3]!.Value.ToArray()
        );
    }
}

// A flow and the message that begins it, which is all a flow strictly needs: enough on its own
// for one that runs to completion from T1, or that only ever waits on effects completing from
// the outside. Everything but the two typed members is inherited.
public abstract class Flow<T1> : Flow
{
    // The message that started the flow. Run is written against it, but a "Handle" method
    // only receives the message that just arrived - so on every invocation after the first
    // it comes back out of the recorded state instead of off the wire.
    private T1 InitialMessage
        => RecordedInitialMessage is T1 message
            ? message
            : throw new InvalidOperationException(
                $"Flow '{Id}' cannot run: no {typeof(T1).Name} was recorded to start it");

    // T1 is the message that begins the flow: "StartOrHandle" runs whether or not the
    // saga already exists, so it is what creates the state.
    //
    // The "sagaId" parameter is how the identity gets in: Wolverine has already
    // worked it out from the message ([SagaIdentity] here) before calling this
    // method, and hands over that value when a string parameter of that name is
    // asked for. Without it the new flow is stored with a null Id and rejected.
    //
    // The DormouseContext parameter gets in the same way: it is registered in the
    // container, so Wolverine treats it as a service to resolve rather than as part of the
    // message, and hands the singleton over on every invocation.
    public Task StartOrHandle(T1 msg, string sagaId, DormouseContext context)
    {
        // The only thing this path does that the others do not: a brand new flow has no Id
        // yet, and the "Handle" methods always run against one that was loaded with its own.
        Id = sagaId;
        return WriteAndRun(msg, context);
    }

    public abstract Task Run(T1 message);

    protected override Task RunFromInitialMessage() => Run(InitialMessage);
}

// The same flow, plus four message types it can be handed once it has started. They are entry
// points and nothing else: each one records the message that arrived and replays Run, exactly
// as the start message does - the difference being that a "Handle" method only runs against a
// flow that already exists.
//
// A sibling of Flow<T1> rather than a subclass of it, so the start path is declared again
// here rather than inherited; see Flow<T1> for what its three parameters are each doing.
public abstract class Flow<T1, T2, T3, T4, T5> : Flow
{
    private T1 InitialMessage
        => RecordedInitialMessage is T1 message
            ? message
            : throw new InvalidOperationException(
                $"Flow '{Id}' cannot run: no {typeof(T1).Name} was recorded to start it");

    public Task StartOrHandle(T1 msg, string sagaId, DormouseContext context)
    {
        Id = sagaId;
        return WriteAndRun(msg, context);
    }

    public Task Handle(T2 msg, DormouseContext context) => WriteAndRun(msg, context);
    public Task Handle(T3 msg, DormouseContext context) => WriteAndRun(msg, context);
    public Task Handle(T4 msg, DormouseContext context) => WriteAndRun(msg, context);
    public Task Handle(T5 msg, DormouseContext context) => WriteAndRun(msg, context);

    public abstract Task Run(T1 message);

    protected override Task RunFromInitialMessage() => Run(InitialMessage);
}

// One FlowState entry: what kind of entry it is, which effect it belongs to (absent for
// messages), the type its payload was written as, and the payload itself.
public readonly record struct FlowStateEntry(StateType StateType, int? EffectId, Type Type, byte[] Payload);
