namespace Bunkum.CustomHttpListener.Parsing;

public enum Method
{
    Invalid,
    Get,
    Put,
    Post,
    Delete,
    Head,
    Options,
    Trace,
    Patch,
    Connect,
    Invite,
    Ack,
    Bye,
    Cancel,
    Register,
    Prack,
    Subscribe,
    Notify,
    Publish,
    Info,
    Refer,
    Message,
    Update
}

public static class MethodUtils
{
    public static Method FromString(ReadOnlySpan<char> str)
    {
        // ReSharper disable once LoopCanBeConvertedToQuery (this literally won't compile)
        foreach (Method m in Enum.GetValues<Method>())
        {
            if (m.ToString().ToUpperInvariant().AsSpan().SequenceEqual(str)) return m;
        }

        return Method.Invalid;
    }
}