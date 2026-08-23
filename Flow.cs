using System.Buffers.Binary;
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
    
    protected internal object? InitialMessage { get; set; }
    
    private DormouseContext? _context;
    protected DormouseContext Context
        => _context ?? throw new InvalidOperationException(
            $"Flow '{Id}' has no {nameof(DormouseContext)}: it is only set while a message is being handled");
    
    private readonly AsyncSignal _continueSignal = new();
    private readonly AsyncSignal _flushSignal = new();
    
    
    private Flow Initialize(DormouseContext context)
    {
        _atEffectId = 0;
        SagaId = Id + "@" + GetType().SimpleQualifiedName();
        _context = context;
        
        var cachedFlow = context.FlowsCache.GetOrSet(Id!, this); //am i self?
        if (cachedFlow.AtCheckPoint != AtCheckPoint)
        {
            Deserialize();
            _ = RunFromInitialMessage();
            context.FlowsCache.Set(Id!, this); //overwrites if cache is out of sync
        }

        return cachedFlow;
    }

    private void Deserialize()
    {
        foreach (var bytes in FlowState)
        {
            var (stateType, index, type, payload) = Decode(bytes);
            if (stateType == StateType.Effect)
                _effects[index] = payload;
            else
            {
                var message = JsonSerializer.Deserialize(payload, type)!;
                if (stateType == StateType.InitialMessage)
                    InitialMessage = message;
                else
                    _inbox[index] = message;
            }
        }
    }
    
    private Task WaitForFlush() => _flushSignal.WaitAsync();
    
    public async Task Handle(Captured _, DormouseContext context)
    {
        var cachedFlow = Initialize(context);
        cachedFlow._continueSignal.Notify();
        
        await WaitForFlush();
        
        FlowState = cachedFlow.FlowState;
        AtCheckPoint = cachedFlow.AtCheckPoint;
    }

    protected internal async Task WriteAndRun<T>(T msg, DormouseContext context)
    {
        var cachedFlow = Initialize(context);
        
        if (cachedFlow.InitialMessage == null)
            cachedFlow.SetInitialMessage(msg!);
        else 
            cachedFlow.AppendMessage(msg!);
        
        _continueSignal.Notify();

        await WaitForFlush();
        
        FlowState = cachedFlow.FlowState;
        AtCheckPoint = cachedFlow.AtCheckPoint;
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
                    return (T) msg;
                }
            }

            _flushSignal.Notify();
            await _continueSignal.WaitAsync();
        }
    }

    public Task<T> Capture<T>(Func<T> func) => Capture(() => Task.FromResult(func()));
    public async Task<T> Capture<T>(Func<Task<T>> func)
    {
        var effectId = _atEffectId++;

        if (_effects.TryGetValue(effectId, out var stored))
            return JsonSerializer.Deserialize<T>(stored)!;

        var result = await func();
        
        AppendEffect(effectId, result!);
        return result;
    }

    private void AppendEffect(int effectId, object value)
    {
        FlowState.Add(
            Encode(
                StateType.Effect, 
                effectId, 
                value.GetType(), 
                JsonSerializer.SerializeToUtf8Bytes(value)
            )
        );

        AtCheckPoint = Guid.NewGuid();
    }

    private void AppendMessage(object message)
    {
        FlowState.Add(
            Encode(
                StateType.Message,
                index: -1,
                message.GetType(), //todo handle compiler generated types like ienumerable and similar - convert to lists
                JsonSerializer.SerializeToUtf8Bytes(message)
            )
        );

        AtCheckPoint = Guid.NewGuid();
    }

    private void SetInitialMessage(object message)
    {
        FlowState.Add(
            Encode(
                StateType.InitialMessage,
                index: -1,
                message.GetType(), //todo handle compiler generated types like ienumerable and similar - convert to lists
                JsonSerializer.SerializeToUtf8Bytes(message)
            )
        );

        AtCheckPoint = Guid.NewGuid();
    }

    private static byte[] Encode(StateType stateType, int index, Type type, byte[] payload)
    {
        var id = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(id, index);

        return ByteArrayMarshaller.Serialize([(byte)stateType], id, type.SerializeType(), payload);
    }

    private static FlowStateEntry Decode(byte[] entry)
    {
        var fields = ByteArrayMarshaller.Deserialize(entry, expectedCount: 4);

        return new FlowStateEntry(
            (StateType)fields[0]!.Value.Span[0],
            BinaryPrimitives.ReadInt32LittleEndian(fields[1]!.Value.Span),
            fields[2]!.Value.ToArray().ResolveType()!,
            fields[3]!.Value.ToArray()
        );
    }
}

public abstract class Flow<T1> : Flow
{
    public Task StartOrHandle(T1 msg, string sagaId, DormouseContext context)
    {
        Id = sagaId;
        return WriteAndRun(msg, context);
    }

    protected abstract Task Run(T1 message);

    protected override Task RunFromInitialMessage() => Run((T1)InitialMessage!); //todo handle null message
}

public abstract class Flow<T1, T2, T3, T4, T5> : Flow
{
    public Task StartOrHandle(T1 msg, string sagaId, DormouseContext context)
    {
        Id = sagaId;
        return WriteAndRun(msg, context);
    }

    public Task Handle(T2 msg, DormouseContext context) => WriteAndRun(msg, context);
    public Task Handle(T3 msg, DormouseContext context) => WriteAndRun(msg, context);
    public Task Handle(T4 msg, DormouseContext context) => WriteAndRun(msg, context);
    public Task Handle(T5 msg, DormouseContext context) => WriteAndRun(msg, context);

    protected abstract Task Run(T1 message);

    protected override Task RunFromInitialMessage() => Run((T1) InitialMessage!); //todo consider null!
}

// One FlowState entry: what kind of entry it is, where it sits on that kind's own sequence -
// captures and messages are numbered independently, so an Index only means anything alongside
// the StateType - the type its payload was written as, and the payload itself.
public readonly record struct FlowStateEntry(StateType StateType, int Index, Type Type, byte[] Payload);
