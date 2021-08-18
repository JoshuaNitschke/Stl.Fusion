using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Stl.Text;

namespace Stl.Fusion.EntityFramework.Conversion
{
    public class SymbolToStringValueConverter : ValueConverter<Symbol, string>
    {
        public SymbolToStringValueConverter(ConverterMappingHints? mappingHints = null)
            : base(
                v => v.Value,
                v => new Symbol(v),
                mappingHints)
        { }
    }
}