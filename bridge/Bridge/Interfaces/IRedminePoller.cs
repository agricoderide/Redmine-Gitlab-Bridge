using System.Threading;

namespace Bridge.Services;

public interface IRedminePoller
{
    Task PollOnceAsync(CancellationToken ct = default);
}
