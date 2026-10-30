namespace Auction.Api.Common.Converters;

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Two-way Enum JsonConverter:
/// - OUT (BE -> Client): Serializes C# Enum to PascalCase String (e.g. "Active", "PendingPayment").
/// - IN (Client -> BE): Parses String (case-insensitive) into C# Enum (integer).
/// - Error: Throws clear Sentence case English error detailing valid values if invalid.
/// </summary>
public sealed class PascalCaseJsonStringEnumConverter : JsonConverterFactory
{
    private readonly bool _allowIntegerValues;

    public PascalCaseJsonStringEnumConverter(bool allowIntegerValues = false)
    {
        _allowIntegerValues = allowIntegerValues;
    }

    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert.IsEnum || (Nullable.GetUnderlyingType(typeToConvert)?.IsEnum ?? false);
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var enumType = Nullable.GetUnderlyingType(typeToConvert) ?? typeToConvert;
        var isNullable = Nullable.GetUnderlyingType(typeToConvert) != null;

        var converterType = isNullable
            ? typeof(NullableEnumConverter<>).MakeGenericType(enumType)
            : typeof(NonNullableEnumConverter<>).MakeGenericType(enumType);

        return (JsonConverter)Activator.CreateInstance(converterType, new object[] { _allowIntegerValues })!;
    }

    public sealed class NonNullableEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        private readonly bool _allowIntegerValues;

        public NonNullableEnumConverter() : this(false)
        {
        }

        public NonNullableEnumConverter(bool allowIntegerValues)
        {
            _allowIntegerValues = allowIntegerValues;
        }

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var stringValue = reader.GetString();
                if (string.IsNullOrWhiteSpace(stringValue))
                {
                    throw new JsonException($"Enum value for '{typeof(T).Name}' cannot be empty. Valid values are: {string.Join(", ", Enum.GetNames<T>())}.");
                }

                if (!_allowIntegerValues && long.TryParse(stringValue, out _))
                {
                    throw new JsonException($"Invalid value '{stringValue}' for enum '{typeof(T).Name}'. Valid values are: {string.Join(", ", Enum.GetNames<T>())}.");
                }

                if (stringValue.Contains(',') && !typeof(T).IsDefined(typeof(FlagsAttribute), false))
                {
                    throw new JsonException($"Invalid value '{stringValue}' for enum '{typeof(T).Name}'. Valid values are: {string.Join(", ", Enum.GetNames<T>())}.");
                }

                if (Enum.TryParse<T>(stringValue, ignoreCase: true, out var result) && Enum.IsDefined(typeof(T), result))
                {
                    return result;
                }

                throw new JsonException($"Invalid value '{stringValue}' for enum '{typeof(T).Name}'. Valid values are: {string.Join(", ", Enum.GetNames<T>())}.");
            }

            if (_allowIntegerValues && reader.TokenType == JsonTokenType.Number)
            {
                var intVal = reader.GetInt32();
                if (Enum.IsDefined(typeof(T), intVal))
                {
                    return (T)Enum.ToObject(typeof(T), intVal);
                }
                throw new JsonException($"Integer value '{intVal}' is not defined in enum '{typeof(T).Name}'. Valid values are: {string.Join(", ", Enum.GetNames<T>())}.");
            }

            throw new JsonException($"Expected string value for enum '{typeof(T).Name}', but got {reader.TokenType}. Valid values are: {string.Join(", ", Enum.GetNames<T>())}.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }
    }

    public sealed class NullableEnumConverter<T> : JsonConverter<T?> where T : struct, Enum
    {
        private readonly NonNullableEnumConverter<T> _underlying;

        public NullableEnumConverter() : this(false)
        {
        }

        public NullableEnumConverter(bool allowIntegerValues)
        {
            _underlying = new NonNullableEnumConverter<T>(allowIntegerValues);
        }

        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            return _underlying.Read(ref reader, typeof(T), options);
        }

        public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                _underlying.Write(writer, value.Value, options);
            }
        }
    }
}
