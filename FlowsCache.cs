namespace Dormouse;

public class FlowsCache
{
    private readonly Dictionary<string, Flow> _flows = new();
    private readonly Lock _lock = new();
    
    //consider when the flow should be removed from cache?
    
    public Flow GetOrSet(string id, Flow flow)
    {
        lock (_lock)
            if (_flows.ContainsKey(id))
                return _flows[id];
            else
                return _flows[id] = flow;
    }
    
    public void Set(string id, Flow flow)
    {
        lock (_lock)
            _flows[id] = flow;
    }
}