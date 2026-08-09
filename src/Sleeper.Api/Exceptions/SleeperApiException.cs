using System.Net;

namespace Sleeper.Api.Exceptions;

public class SleeperApiException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public SleeperApiException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public SleeperApiException(HttpStatusCode statusCode, string message, Exception innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}
