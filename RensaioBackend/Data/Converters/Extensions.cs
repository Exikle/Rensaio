using System.Text.Json;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace RensaioBackend.Data.Converters
{
    public static class Extensions
    {
        public static JsonSerializerOptions SerializerOptions { get; } = new JsonSerializerOptions { WriteIndented = false };

        public static PropertyBuilder<TProperty> HasJsonConversion<TProperty>(this PropertyBuilder<TProperty> propertyBuilder)
        {
            var ret = propertyBuilder.HasConversion(
                v => JsonSerializer.Serialize(v, SerializerOptions),
                v => JsonSerializer.Deserialize<TProperty>(v, SerializerOptions) ?? default!
            );
            ret.Metadata.SetValueComparer(GenericValueComparer.Create<TProperty>());
            return ret;
        }
        public static PropertyBuilder<List<string>> HasStringSplit(this PropertyBuilder<List<string>> propertyBuilder)
        {
            var ret = propertyBuilder.HasConversion(
                        v => string.Join(',', v),
                        v => v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
                    );
            ret.Metadata.SetValueComparer(GenericValueComparer.Create<List<string>>());
            return ret;
        }
        private static readonly GuidToBlob16Converter GuidBlobConverter = new GuidToBlob16Converter();

        public static PropertyBuilder<Guid> HasGuidToBlob16(this PropertyBuilder<Guid> propertyBuilder)
        {
            var ret = propertyBuilder.HasConversion(GuidBlobConverter);
            //ret.Metadata.SetValueComparer(GenericValueComparer.Create<Guid>());
            return ret;
        }
    }
}
