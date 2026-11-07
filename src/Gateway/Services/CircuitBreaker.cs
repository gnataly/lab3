namespace Gateway.Services;

/// <summary>
/// Простой circuit breaker: после N последовательных отказов переходит в состояние Open
/// и отдаёт fallback без реального запроса; через timeout выполняет пробный запрос (Half-Open),
/// чтобы проверить, восстановился ли сервис.
/// </summary>
public sealed class CircuitBreaker
{
    private readonly object _gate = new();
    private readonly int _failureThreshold;
    private readonly TimeSpan _timeout;
    private int _failureCount;
    private DateTimeOffset? _openedAt;
    private bool _halfOpenInProgress;

    public CircuitBreaker(int failureThreshold, TimeSpan timeout)
    {
        _failureThreshold = failureThreshold;
        _timeout = timeout;
    }

    /// <summary>Разрешён ли реальный вызов (Closed) либо это пробный запрос (Half-Open).</summary>
    public bool AllowAttempt()
    {
        lock (_gate)
        {
            if (_failureCount < _failureThreshold)
            {
                return true; // Closed
            }

            if (_openedAt == null)
            {
                return true;
            }

            if (DateTimeOffset.UtcNow - _openedAt.Value < _timeout)
            {
                return false; // Open — сразу fallback, в сервис не ходим
            }

            if (_halfOpenInProgress)
            {
                return false; // пробный запрос уже идёт
            }

            _halfOpenInProgress = true;
            return true; // Half-Open — разрешаем один пробный запрос
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _failureCount = 0;
            _openedAt = null;
            _halfOpenInProgress = false;
        }
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            _failureCount++;
            _openedAt = DateTimeOffset.UtcNow;
            _halfOpenInProgress = false;
        }
    }
}