using System.Net.Http.Json;
using System.Text.Json;
using Gateway.Contracts;

namespace Gateway.Services;

public class GatewayOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _carsClient;
    private readonly HttpClient _rentalClient;
    private readonly HttpClient _paymentClient;
    private readonly CircuitBreaker _carsBreaker;
    private readonly CircuitBreaker _rentalBreaker;
    private readonly CircuitBreaker _paymentBreaker;
    private readonly RetryQueueService _retryQueue;

    public GatewayOrchestrator(
        HttpClient carsClient,
        HttpClient rentalClient,
        HttpClient paymentClient,
        CircuitBreaker carsBreaker,
        CircuitBreaker rentalBreaker,
        CircuitBreaker paymentBreaker,
        RetryQueueService retryQueue)
    {
        _carsClient = carsClient;
        _rentalClient = rentalClient;
        _paymentClient = paymentClient;
        _carsBreaker = carsBreaker;
        _rentalBreaker = rentalBreaker;
        _paymentBreaker = paymentBreaker;
        _retryQueue = retryQueue;
    }

    // ----- Чтение: Circuit Breaker + fallback -----

    public async Task<PaginationResponse> GetCarsAsync(int page, int size, bool showAll)
    {
        // Данные Car критичны -> при недоступности 500.
        return await ExecuteAsync(_carsBreaker, async () =>
        {
            var response = await _carsClient.GetAsync($"/api/v1/cars?page={page}&size={size}&showAll={showAll}");
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<PaginationResponse>(JsonOptions))!;
        }, critical: true, fallback: null!);
    }

    public async Task<RentalResponse> GetRentalAsync(string username, Guid rentalUid)
    {
        var rental = await GetRentalInternalAsync(rentalUid); // Rental критичен -> 500
        EnsureOwner(rental, username);
        return await BuildRentalResponseAsync(rental);
    }

    public async Task<IReadOnlyList<RentalResponse>> GetRentalsAsync(string username)
    {
        var rentals = await ExecuteAsync(_rentalBreaker, async () =>
        {
            var response = await _rentalClient.GetAsync($"/api/v1/rental?username={Uri.EscapeDataString(username)}");
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<List<RentalInternal>>(JsonOptions)) ?? new List<RentalInternal>();
        }, critical: true, fallback: null!);

        var result = new List<RentalResponse>();
        foreach (var rental in rentals)
        {
            result.Add(await BuildRentalResponseAsync(rental));
        }

        return result;
    }

    // ----- Запись: откат / очередь -----

    public async Task<CreateRentalResponse> CreateRentalAsync(string username, CreateRentalRequest request)
    {
        // 1. Car: проверить, что машина существует, и зарезервировать её.
        CarResponse car;
        try
        {
            car = await GetCarAsync(request.CarUid);
            await ReserveAsync(request.CarUid);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            throw new ServiceUnavailableException("Car Service unavailable");
        }

        // 2. Payment: создать оплату. Если недоступен -> откат резерва, 503.
        PaymentInfo payment;
        try
        {
            var totalPrice = CalculateDays(request.DateFrom, request.DateTo) * car.Price;
            payment = await CreatePaymentAsync(totalPrice);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            await SafeUnreserveAsync(request.CarUid);
            throw new ServiceUnavailableException("Payment Service unavailable");
        }

        // 3. Rental: создать аренду. Если недоступен -> откат оплаты и резерва, 503.
        try
        {
            var rental = await CreateRentalInternalAsync(username, payment.PaymentUid!.Value, request);
            return new CreateRentalResponse(
                rental.RentalUid,
                rental.Status,
                rental.CarUid,
                rental.DateFrom,
                rental.DateTo,
                payment);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            await SafeCancelPaymentAsync(payment.PaymentUid!.Value);
            await SafeUnreserveAsync(request.CarUid);
            throw new ServiceUnavailableException("Rental Service unavailable");
        }
    }

    public async Task FinishRentalAsync(string username, Guid rentalUid)
    {
        var rental = await GetRentalInternalAsync(rentalUid);
        EnsureOwner(rental, username);

        await UnreserveAsync(rental.CarUid);
        var finishResponse = await _rentalClient.PostAsync($"/api/v1/rental/{rentalUid}/finish", null);
        finishResponse.EnsureSuccessStatusCode();
    }

    public async Task CancelRentalAsync(string username, Guid rentalUid)
    {
        var rental = await GetRentalInternalAsync(rentalUid);
        EnsureOwner(rental, username);

        // 1. Снимаем резерв с автомобиля.
        await UnreserveAsync(rental.CarUid);

        // 2. Отмена аренды: если Rental недоступен -> в очередь (повтор пока не успех).
        await RunOrQueue(() => CancelRentalInternalAsync(rentalUid));

        // 3. Отмена оплаты: если Payment недоступен -> в очередь, пользователю уже успех.
        await RunOrQueue(() => CancelPaymentInternalAsync(rental.PaymentUid));
    }

    // ----- Внутренние вызовы -----

    private async Task<CarResponse> GetCarAsync(Guid carUid)
    {
        var response = await _carsClient.GetAsync($"/api/v1/cars/{carUid}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CarResponse>(JsonOptions))!;
    }

    private async Task<RentalInternal> GetRentalInternalAsync(Guid rentalUid)
    {
        // Rental критичен: недоступность пробрасывается наружу как 500.
        var response = await _rentalClient.GetAsync($"/api/v1/rental/{rentalUid}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RentalInternal>(JsonOptions))!;
    }

    private async Task<RentalResponse> BuildRentalResponseAsync(RentalInternal rental)
    {
        // Car и Payment в ответе аренды некритичны: при недоступности подставляем пустой объект {}.
        var car = await ExecuteAsync(_carsBreaker, async () =>
        {
            var car = await GetCarAsync(rental.CarUid);
            return new CarInfo { CarUid = car.CarUid, Brand = car.Brand, Model = car.Model, RegistrationNumber = car.RegistrationNumber };
        }, critical: false, fallback: new CarInfo());

        var payment = await ExecuteAsync(_paymentBreaker, async () =>
        {
            var response = await _paymentClient.GetAsync($"/api/v1/payments/{rental.PaymentUid}");
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<PaymentInfo>(JsonOptions))!;
        }, critical: false, fallback: new PaymentInfo());

        return new RentalResponse(rental.RentalUid, rental.Status, rental.DateFrom, rental.DateTo, car, payment);
    }

    private async Task ReserveAsync(Guid carUid)
    {
        var response = await _carsClient.PostAsync($"/api/v1/cars/{carUid}/reserve", null);
        response.EnsureSuccessStatusCode();
    }

    private async Task UnreserveAsync(Guid carUid)
    {
        var response = await _carsClient.PostAsync($"/api/v1/cars/{carUid}/unreserve", null);
        response.EnsureSuccessStatusCode();
    }

    private async Task<PaymentInfo> CreatePaymentAsync(int price)
    {
        var response = await _paymentClient.PostAsJsonAsync("/api/v1/payments", new { price }, JsonOptions);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PaymentInfo>(JsonOptions))!;
    }

    private async Task<RentalInternal> CreateRentalInternalAsync(string username, Guid paymentUid, CreateRentalRequest request)
    {
        var response = await _rentalClient.PostAsJsonAsync(
            "/api/v1/rental",
            new { username, paymentUid, carUid = request.CarUid, dateFrom = request.DateFrom, dateTo = request.DateTo },
            JsonOptions);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RentalInternal>(JsonOptions))!;
    }

    private async Task CancelRentalInternalAsync(Guid rentalUid)
    {
        var response = await _rentalClient.PostAsync($"/api/v1/rental/{rentalUid}/cancel", null);
        response.EnsureSuccessStatusCode();
    }

    private async Task CancelPaymentInternalAsync(Guid paymentUid)
    {
        var response = await _paymentClient.PostAsync($"/api/v1/payments/{paymentUid}/cancel", null);
        response.EnsureSuccessStatusCode();
    }

    // ----- Вспомогательные методы -----

    private async Task<T> ExecuteAsync<T>(CircuitBreaker breaker, Func<Task<T>> action, bool critical, T fallback)
    {
        if (!breaker.AllowAttempt())
        {
            return CriticalOrFallback(critical, fallback);
        }

        try
        {
            var result = await action();
            breaker.RecordSuccess();
            return result;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            breaker.RecordFailure();
            return CriticalOrFallback(critical, fallback);
        }
    }

    private static T CriticalOrFallback<T>(bool critical, T fallback)
    {
        if (critical)
        {
            throw new CriticalDependencyException();
        }

        return fallback;
    }

    private async Task RunOrQueue(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            _retryQueue.Enqueue(operation);
        }
    }

    private async Task SafeUnreserveAsync(Guid carUid)
    {
        try
        {
            await UnreserveAsync(carUid);
        }
        catch
        {
            // Игнорируем — это компенсирующий вызов.
        }
    }

    private async Task SafeCancelPaymentAsync(Guid paymentUid)
    {
        try
        {
            await CancelPaymentInternalAsync(paymentUid);
        }
        catch
        {
            // Игнорируем — это компенсирующий вызов.
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or OperationCanceledException or TimeoutException;

    private static void EnsureOwner(RentalInternal rental, string username)
    {
        if (!string.Equals(rental.Username, username, StringComparison.Ordinal))
        {
            throw new RentalNotFoundException();
        }
    }

    public static int CalculateDays(string dateFrom, string dateTo)
    {
        var from = DateOnly.Parse(dateFrom);
        var to = DateOnly.Parse(dateTo);
        return Math.Abs(to.DayNumber - from.DayNumber);
    }
}

public sealed class RentalNotFoundException : Exception;