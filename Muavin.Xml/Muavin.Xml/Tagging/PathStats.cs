using System;
using System.Collections.Generic;


namespace Muavin.Xml.Tagging;

public sealed class PathStats
{
    public string Kind { get; set; } = "Element";
    public string Path { get; set; } = "";
    public long Count { get; set;} 
    public string? NamespaceUri { get; set; }

    public HashSet<string> AttributeNames { get; } = new(StringComparer.Ordinal);
    public List<string> SampleValues { get; } = new();
}

