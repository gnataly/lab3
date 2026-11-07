namespace Gateway.Services;

/// <summary>Критичный источник недоступен → HTTP 500.</summary>
public sealed class CriticalDependencyException : Exception
{
    public CriticalDependencyException(string message = "Dependency unavailable") : base(message)
    {
    }
}

/// <summary>Сервис, участвующий в операции, недоступен → HTTP 503.</summary>
public sealed class ServiceUnavailableException : Exception
{
    public ServiceUnavailableException(string message) : base(message)
    {
    }
}