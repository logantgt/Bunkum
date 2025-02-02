using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Bunkum.CustomHttpListener.Parsing;

[SuppressMessage("ReSharper", "UnusedMember.Global")]
public enum ContentType
{
    [ContentType("text/html; charset=utf-8", false)] Html,
    [ContentType("text/plain", false)] Plaintext,
    [ContentType("text/xml", true)] Xml,
    [ContentType("application/json", true)] Json,
    [ContentType("application/octet-stream", false)] BinaryData,
    [ContentType("image/png", false)] Png,
    [ContentType("image/jpeg", false)] Jpeg,
}

[AttributeUsage(AttributeTargets.Field)]
internal class ContentTypeAttribute : Attribute
{
    internal string Name { get; }
    internal bool IsSerializable { get; }

    internal ContentTypeAttribute(string name, bool isSerializable)
    {
        this.Name = name;
        this.IsSerializable = isSerializable;
    }
}

public static class ContentTypeExtensions
{
    private static ContentTypeAttribute GetAttribute(ContentType contentType)
    {
        Type type = typeof(ContentType);
        
        MemberInfo? memberInfo = type.GetMember(contentType.ToString()).FirstOrDefault();
        Debug.Assert(memberInfo != null);
        
        ContentTypeAttribute? attribute = memberInfo.GetCustomAttribute<ContentTypeAttribute>();
        Debug.Assert(attribute != null);

        return attribute;
    }
    
    internal static string GetName(this ContentType contentType)
    {
        return GetAttribute(contentType).Name;
    }
    
    public static bool IsSerializable(this ContentType contentType)
    {
        return GetAttribute(contentType).IsSerializable;
    }
}