using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Wolverine;

namespace Dormouse;

public abstract class Flow : Saga
{
    // Wolverine's saga storage requires a non-null identity, and it does not assign
    // one when it creates the saga - so the flow has to pick it up itself.
    public string? Id { get; set; }
    private string? SagaId { get; set; }

    public List<byte[]> FlowState { get; set; } = [];
    public Guid AtCheckPoint { get; set; }

    private readonly Dictionary<int, byte[]> _effects = new();
    private int _atEffectId;

    private readonly List<object> _inbox = new();
    // Messages are numbered on a sequence of their own, independently of captures, so an entry's
    // index only means anything alongside its StateType.
    private int _atMessageId;

    protected internal object? InitialMessage { get; set; }

    private readonly Lock _lock = new();
    private bool _taken;
    
    private bool Take()
    {
        lock (_lock)
            if (_taken)
                return false;
            else
                return _taken = true;
    }

    private void Free()
    {
        lock (_lock)
            _taken = false;
    }

    private bool _started = false;
    
    private Task? _run;

    private DormouseContext? _context;
    protected DormouseContext Context
        => _context ?? throw new InvalidOperationException(
            $"Flow '{Id}' has no {nameof(DormouseContext)}: it is only set while a message is being handled");

    private readonly AsyncSignal _continueSignal = new();
    private readonly AsyncSignal _flushSignal = new();
    
    // Works out which instance actually runs this flow. A running flow lives in the cache, and a
    // message for it has to reach that instance rather than the one storage just handed back:
    // the running one is where Run is parked, and its state is the state that is current.
    private Flow Initialize(DormouseContext context)
    {
        SagaId = Id + "@" + context.TypeHelper.SimpleQualifiedName(GetType());
        _context = context;
        // Taken before it can be seen: once GetOrSet has stored this instance, another handler
        // may find it there and try to take it, and it must not succeed while this one is still
        // working on it.
        _taken = true;

        var cachedFlow = context.FlowsCache.GetOrSet(Id!, this);
        if (!ReferenceEquals(cachedFlow, this))
        {
            var taken = cachedFlow.Take();
            if (cachedFlow.AtCheckPoint == AtCheckPoint && taken)
                return cachedFlow;

            //use self
            if (taken)
                cachedFlow.Free();
        }

        // A started flow's own state is the current one, and hydrating it again would put messages
        // Message<T> has already consumed back into the inbox. Only a fresh instance, handed back
        // by storage with nothing but FlowState, has to be built up from what was recorded.
        if (!_started)
            Deserialize();

        return this;
    }

    private void Deserialize()
    {
        InitialMessage = null;
        _atMessageId = 0;

        foreach (var bytes in FlowState)
        {
            var (stateType, index, type, payload) = Decode(bytes);
            if (stateType == StateType.Effect)
            {
                _effects[index] = payload;
                continue;
            }

            var message = JsonSerializer.Deserialize(payload, type)!;
            if (stateType == StateType.InitialMessage)
                InitialMessage = message;
            else
                _inbox.Add(message);

            _atMessageId = index + 1;
        }
    }

    private Task WaitForFlush() => _flushSignal.WaitAsync();

    private async Task Drive()
    {
        if (!_started)
        {
            _run = RunFromInitialMessage();
            // Only once Run exists: a start that throws (no start message recorded) leaves nothing
            // to drive, so the next message must try to start again rather than notify a null run.
            _started = true;
        }
        else
            _continueSignal.Notify();

        await Task.WhenAny(WaitForFlush(), _run!);

        if (_run is not { IsFaulted: true })
            return;

        var error = _run.Exception!.InnerException ?? _run.Exception;
        ExceptionDispatchInfo.Capture(error).Throw();
    }

    // Initialize hands back a flow that is taken, and it stays taken for as long as this handler
    // is working on it - through the write, the drive, and the copy of state back out - so the
    // release has to be the last thing to happen, whether the handler got that far or threw.
    public async Task Handle(Captured c, DormouseContext context)
    {
        var flow = Initialize(context);
        try
        {
            await flow.Drive();

            FlowState = flow.FlowState;
            AtCheckPoint = flow.AtCheckPoint;
        }
        finally
        {
            flow.Free();
        }
    }

    protected internal async Task WriteAndRun<T>(T msg, DormouseContext context)
    {
        var flow = Initialize(context);
        try
        {
            if (flow.InitialMessage == null)
                flow.SetInitialMessage(msg!);
            else
                flow.AppendMessage(msg!);

            await flow.Drive();

            FlowState = flow.FlowState;
            AtCheckPoint = flow.AtCheckPoint;
        }
        finally
        {
            flow.Free();
        }
    }

    protected abstract Task RunFromInitialMessage();

    public async Task<T> Message<T>()
    {
        while (true)
        {
            for (var i = 0; i < _inbox.Count; i++)
            {
                if (_inbox[i] is T)
                {
                    var msg = _inbox[i];
                    _inbox.RemoveAt(i);
                    //also remove from FlowState?
                    return (T) msg;
                }
            }

            _flushSignal.Notify();
            await _continueSignal.WaitAsync();
        }
    }

    public Task<T> Capture<T>(Func<T> func, bool flush = false) => Capture(() => Task.FromResult(func()), flush);
    public async Task<T> Capture<T>(Func<Task<T>> func, bool flush = false)
    {
        var effectId = _atEffectId++;

        if (_effects.TryGetValue(effectId, out var stored))
            return JsonSerializer.Deserialize<T>(stored)!;

        var result = await func();

        // Written against the static type rather than the value's own: a null result has no type
        // to ask, and it is the type a replay has to deserialize back to.
        AppendEffect(effectId, typeof(T), JsonSerializer.SerializeToUtf8Bytes(result));

        if (flush)
        {
            _flushSignal.Notify();
            await _continueSignal.WaitAsync();
        }

        return result;
    }

    private void AppendEffect(int effectId, Type type, byte[] payload)
    {
        _effects[effectId] = payload;
        FlowState.Add(Encode(StateType.Effect, effectId, type, payload));

        AtCheckPoint = Guid.NewGuid();
    }

    private void AppendMessage(object message)
    {
        _inbox.Add(message);
        RecordMessage(StateType.Message, message);
    }

    private void SetInitialMessage(object message)
    {
        InitialMessage = message;
        RecordMessage(StateType.InitialMessage, message);
    }

    private void RecordMessage(StateType stateType, object message)
    {
        FlowState.Add(
            Encode(
                stateType,
                _atMessageId++,
                message.GetType(), //todo handle compiler generated types like ienumerable and similar - convert to lists
                // Against the runtime type, not the declared one - through "object" this would
                // write an empty "{}" instead of the message's own properties.
                JsonSerializer.SerializeToUtf8Bytes(message, message.GetType())
            )
        );

        AtCheckPoint = Guid.NewGuid();
    }

    // Both go through the context's TypeHelper, so the name a type is written under and the type
    // a name is read back as come from the same caches - which is why neither is static.
    private byte[] Encode(StateType stateType, int index, Type type, byte[] payload)
    {
        var id = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(id, index);

        return ByteArrayMarshaller.Serialize([(byte)stateType], id, Context.TypeHelper.SerializeType(type), payload);
    }

    private FlowStateEntry Decode(byte[] entry)
    {
        var fields = ByteArrayMarshaller.Deserialize(entry, expectedCount: 4);

        return new FlowStateEntry(
            (StateType)fields[0]!.Value.Span[0],
            BinaryPrimitives.ReadInt32LittleEndian(fields[1]!.Value.Span),
            Context.TypeHelper.ResolveType(fields[2]!.Value),
            fields[3]!.Value.ToArray()
        );
    }
}

public abstract class Flow<T1> : Flow
{
    // Run is written against the message that began the flow, so on every invocation after the
    // first it comes back out of the recorded state rather than off the wire. A flow whose state
    // does not start with one has nothing to run against, and says so rather than casting null
    // - or the wrong message - through.
    private T1 StartMessage
        => InitialMessage is T1 message
            ? message
            : throw new InvalidOperationException(
                $"Flow '{Id}' cannot run: no {typeof(T1).Name} was recorded to start it");

    public Task StartOrHandle(T1 msg, string sagaId, DormouseContext context)
    {
        Id = sagaId;
        return WriteAndRun(msg, context);
    }

    protected abstract Task Run(T1 message);

    protected override Task RunFromInitialMessage() => Run(StartMessage);
}

public abstract class Flow<T1, T2, T3, T4, T5> : Flow
{
    private T1 StartMessage
        => InitialMessage is T1 message
            ? message
            : throw new InvalidOperationException(
                $"Flow '{Id}' cannot run: no {typeof(T1).Name} was recorded to start it");

    public Task StartOrHandle(T1 msg, string sagaId, DormouseContext context)
    {
        Id = sagaId;
        //initialize
        return WriteAndRun(msg, context);
    }

    public Task Handle(T2 msg, DormouseContext context) => WriteAndRun(msg, context);
    public Task Handle(T3 msg, DormouseContext context) => WriteAndRun(msg, context);
    public Task Handle(T4 msg, DormouseContext context) => WriteAndRun(msg, context);
    public Task Handle(T5 msg, DormouseContext context) => WriteAndRun(msg, context);

    protected abstract Task Run(T1 message);

    protected override Task RunFromInitialMessage() => Run(StartMessage);
}

// One FlowState entry: what kind of entry it is, where it sits on that kind's own sequence -
// captures and messages are numbered independently, so an Index only means anything alongside
// the StateType - the type its payload was written as, and the payload itself.
public readonly record struct FlowStateEntry(StateType StateType, int Index, Type Type, byte[] Payload);
