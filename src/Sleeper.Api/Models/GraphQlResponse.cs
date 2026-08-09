using System.Text.Json.Serialization;

namespace Sleeper.Api.Models;

internal sealed record GraphQlResponse<TData>(
    [property: JsonPropertyName("data")] TData? Data,
    [property: JsonPropertyName("errors")] List<GraphQlError>? Errors);

internal sealed record GraphQlError(
    [property: JsonPropertyName("message")] string Message);
