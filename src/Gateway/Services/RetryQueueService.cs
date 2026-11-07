using System.Collections.Concurrent;

namespace Gateway.Services;

/// <summary>
/// Очередь отложенных операций на Gateway. Запросы (например, отмена оплаты/аренды),
/// которые не удалось выполнить из-за недоступности сервиса, ставятся в очередь и
/// повторяются фоновым сервисом, пока не завершатся успехом.
/// </summary>
public sealed class RetryQueueService : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly ConcurrentQueue<Func<Task>> _queue = new();

    public void Enqueue(Func<Task> operation) => _queue.Enqueue(operation);

    public int Count => _queue.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_queue.TryDequeue(out var operation))
            {
                try
                {
                    await operation();
                    // Успех — операция извлечена из очереди.
                }
                catch
                {
                    // Неудача — возвращаем в очередь и пробуем снова позже.
                    _queue.Enqueue(operation);
                }
            }

            try
            {
                await Task.Delay(RetryDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}