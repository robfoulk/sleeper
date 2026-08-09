using System.Net;

namespace Sleeper.Api.Exceptions;

public class SleeperRateLimitException : SleeperApiException
{
    public SleeperRateLimitException()
        : base(HttpStatusCode.TooManyRequests, "Sleeper API rate limit exceeded. Stay under 1000 requests per minute.")
    {
    }

    public SleeperRateLimitException(string message)
        : base(HttpStatusCode.TooManyRequests, message)
    {
    }
}
