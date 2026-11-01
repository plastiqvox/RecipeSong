namespace RecipeSong.Core.Soulseek;

/// <summary>대기 중 전송부터 새 제한을 적용한다. 이미 쓰는 슬롯을 줄이려고 전송을 끊지 않는다.</summary>
public sealed class TransferSlots
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _users = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<object> _waiting = [];
    private int _active;
    private int _slots = 1;
    private int _perUser = 1;
    public int Active { get { lock (_gate) return _active; } }
    public (int Free, int Total) Capacity { get { lock (_gate) return (Math.Max(0, _slots - _active), _slots); } }
    public int Waiting { get { lock (_gate) return _waiting.Count; } }
    public bool HasFree { get { lock (_gate) return _active < _slots; } }
    public void Configure(int slots, int perUser)
    {
        if (slots < 1 || perUser < 1) throw new ArgumentOutOfRangeException(nameof(slots));
        lock (_gate) { _slots = slots; _perUser = perUser; }
    }

    public async Task AcquireAsync(string user, CancellationToken ct)
    {
        var ticket = new object();
        lock (_gate) _waiting.AddLast(ticket);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (_waiting.First?.Value == ticket && _active < _slots && _users.GetValueOrDefault(user) < _perUser)
                    {
                        _waiting.Remove(ticket);
                        _active++;
                        _users[user] = _users.GetValueOrDefault(user) + 1;
                        return;
                    }
                    if (_waiting.First?.Value == ticket && _users.GetValueOrDefault(user) >= _perUser)
                    { _waiting.RemoveFirst(); _waiting.AddLast(ticket); }
                }
                await Task.Delay(100, ct);
            }
        }
        catch { lock (_gate) _waiting.Remove(ticket); throw; }
    }

    public void Release(string user)
    {
        lock (_gate)
        {
            if (_users.GetValueOrDefault(user) == 0) return;
            _users[user]--;
            if (_users[user] == 0) _users.Remove(user);
            _active--;
        }
    }
}
