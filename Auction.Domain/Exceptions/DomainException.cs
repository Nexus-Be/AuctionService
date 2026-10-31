namespace Auction.Domain.Exceptions;

public class DomainException : Exception
{
    public string ErrorCode { get; }
    public int StatusCode { get; }

    public DomainException(string message) : base(message)
    {
        ErrorCode = "DOMAIN_ERROR";
        StatusCode = 400;
    }

    public DomainException(string errorCode, string message, int statusCode = 400) : base(message)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }

    public DomainException(string message, Exception innerException) : base(message, innerException)
    {
        ErrorCode = "DOMAIN_ERROR";
        StatusCode = 400;
    }
}

public class EntityNotFoundException : DomainException
{
    public EntityNotFoundException(string entityName, object key)
        : base("NOT_FOUND", $"Entity '{entityName}' with key '{key}' was not found.", 404)
    {
    }

    public EntityNotFoundException(string errorCode, string entityName, object key)
        : base(errorCode, $"Entity '{entityName}' with key '{key}' was not found.", 404)
    {
    }
}

public class BusinessRuleValidationException : DomainException
{
    public BusinessRuleValidationException(string message)
        : base("BUSINESS_RULE_VIOLATION", message, 400)
    {
    }

    public BusinessRuleValidationException(string errorCode, string message, int statusCode = 400)
        : base(errorCode, message, statusCode)
    {
    }
}
