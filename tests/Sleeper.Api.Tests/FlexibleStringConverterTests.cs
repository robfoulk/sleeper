using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Sleeper.Api.Converters;

namespace Sleeper.Api.Tests;

public class FlexibleStringConverterTests
{
    [Fact]
    public void NumericValue_UsesInvariantCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var options = new JsonSerializerOptions();
            options.Converters.Add(new FlexibleStringConverter());

            JsonSerializer.Deserialize<string>("1.5", options).Should().Be("1.5");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
