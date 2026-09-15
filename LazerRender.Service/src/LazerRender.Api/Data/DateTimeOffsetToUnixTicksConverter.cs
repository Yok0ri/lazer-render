using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LazerRender.Api.Data;

/// <summary>
/// Stores <see cref="DateTimeOffset"/> as UTC ticks (INTEGER) so SQLite can order and compare
/// them. SQLite has no native timestamp type and the EF provider refuses to ORDER BY
/// DateTimeOffset when stored as TEXT.
/// </summary>
public sealed class DateTimeOffsetToUnixTicksConverter() : ValueConverter<DateTimeOffset, long>(
    value => value.UtcTicks,
    ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
