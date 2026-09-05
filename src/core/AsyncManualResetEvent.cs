namespace Dormouse;

public sealed class AsyncSignal
{
    private bool _raised = false;
    private TaskCompletionSource? _waitingTcs;
    private readonly Lock _lock = new();

    public async Task WaitAsync()
    {
        TaskCompletionSource waitingTcs;
        lock (_lock)
        {
            if (_raised)
            {
                _raised = false;
                return;
            }
            
            waitingTcs = _waitingTcs = new TaskCompletionSource();
            _raised = false;
        }

        await waitingTcs.Task;
    }

    public void Notify()
    {
        TaskCompletionSource waitingTcs;
        lock (_lock)
        {
            if (_waitingTcs == null)
            {
                _raised = true;
                return;
            }
            
            waitingTcs = _waitingTcs;
            _waitingTcs = null;
        }
        
        waitingTcs.SetResult();
    }
}