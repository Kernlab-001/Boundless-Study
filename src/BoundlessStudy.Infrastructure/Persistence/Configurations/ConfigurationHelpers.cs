using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BoundlessStudy.Infrastructure.Persistence.Configurations;

/// <summary>
/// Shared EF conventions: UTC round-tripping and string enums. SQLite stores
/// DateTime values as TEXT without a Kind, so the converter restores Utc on read.
/// </summary>
internal static class ConfigurationHelpers
{
    private static readonly ValueConverter<DateTime, DateTime> UtcDateTimeConverter = new(
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static readonly ValueConverter<DateTime?, DateTime?> NullableUtcDateTimeConverter = new(
        value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value,
        value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value);

    public static PropertyBuilder<DateTime> IsUtc(this PropertyBuilder<DateTime> property) =>
        property.HasConversion(UtcDateTimeConverter).HasColumnType("TEXT");

    public static PropertyBuilder<DateTime?> IsUtc(this PropertyBuilder<DateTime?> property) =>
        property.HasConversion(NullableUtcDateTimeConverter).HasColumnType("TEXT");

    public static PropertyBuilder<TEnum> IsStringEnum<TEnum>(this PropertyBuilder<TEnum> property, int maxLength)
        where TEnum : struct, Enum =>
        property.HasConversion<string>().HasMaxLength(maxLength);

    public static PropertyBuilder<TEnum?> IsStringEnum<TEnum>(this PropertyBuilder<TEnum?> property, int maxLength)
        where TEnum : struct, Enum =>
        property.HasConversion<string?>().HasMaxLength(maxLength);
}
