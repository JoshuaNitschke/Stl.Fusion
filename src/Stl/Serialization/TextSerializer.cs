namespace Stl.Serialization;

public sealed class TextSerializer : ITextSerializer
{
    public static ITextSerializer Default { get; set; } = SystemJsonSerializer.Default;

    public ITextReader Reader { get; }
    public ITextWriter Writer { get; }

    public TextSerializer(ITextReader reader, ITextWriter writer)
    {
        Reader = reader;
        Writer = writer;
    }

    public ITextSerializer<T> ToTyped<T>(Type? serializedType = null)
        => new TextSerializer<T>(
            Reader.ToTyped<T>(serializedType),
            Writer.ToTyped<T>(serializedType));

    // ITextReader, ITextWriter impl.

    public object? Read(string data, Type type)
        => Reader.Read(data, type);
    public string Write(object? value, Type type)
        => Writer.Write(value, type);

    ITextReader<T> ITextReader.ToTyped<T>(Type? serializedType)
        => Reader.ToTyped<T>(serializedType);
    ITextWriter<T> ITextWriter.ToTyped<T>(Type? serializedType)
        => Writer.ToTyped<T>(serializedType);
}

public class TextSerializer<T> : ITextSerializer<T>
{
    public static ITextSerializer<T> Default => TextSerializer.Default.ToTyped<T>();

    public ITextReader<T> Reader { get; }
    public ITextWriter<T> Writer { get; }

    public TextSerializer(ITextReader<T> reader, ITextWriter<T> writer)
    {
        Reader = reader;
        Writer = writer;
    }

    // ITextReader<T>, ITextWriter<T> impl.

    public T Read(string data)
        => Reader.Read(data);
    public string Write(T value)
        => Writer.Write(value);
}
