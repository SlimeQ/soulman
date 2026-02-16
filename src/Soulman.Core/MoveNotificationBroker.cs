namespace Soulman;

public class MoveNotificationBroker
{
    private readonly object _sync = new();
    private Action<int, string>? _handler;

    public void Subscribe(Action<int, string> handler)
    {
        lock (_sync)
        {
            _handler += handler;
        }
    }

    public void Unsubscribe(Action<int, string> handler)
    {
        lock (_sync)
        {
            _handler -= handler;
        }
    }

    public void Publish(int count, string destination)
    {
        Action<int, string>? handler;
        lock (_sync)
        {
            handler = _handler;
        }

        handler?.Invoke(count, destination);
    }
}
