using Sleeper.Api.Exceptions;

namespace Sleeper.McpServer.Tools;

internal static class ToolSupport
{
    public const string DefaultLeagueId = "1312539280601522176";

    public static string? Required(string? value, string name)
        => string.IsNullOrWhiteSpace(value) ? $"Error: {name} is required." : null;

    public static string? Positive(int value, string name)
        => value <= 0 ? $"Error: {name} must be greater than zero." : null;

    public static string? Between(int value, int minimum, int maximum, string name)
        => value < minimum || value > maximum
            ? $"Error: {name} must be between {minimum} and {maximum}."
            : null;

    public static async Task<string> TryAsync(Func<Task<string>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SleeperRateLimitException)
        {
            return "Error: Sleeper API rate limit exceeded. Try again shortly.";
        }
        catch (SleeperApiException ex)
        {
            return $"Error: Sleeper API returned {(int)ex.StatusCode} {ex.StatusCode}.";
        }
        catch (HttpRequestException ex)
        {
            return $"Error: Sleeper API request failed: {ex.Message}";
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}