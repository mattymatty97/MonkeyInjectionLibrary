using System;

namespace InjectionLibrary.Attributes;

[AttributeUsage(AttributeTargets.Interface, AllowMultiple = true)]
public sealed class InjectInterfaceAttribute(string typeName, string assemblyName = "Assembly-CSharp.dll") : Attribute
{
    public string AssemblyName => assemblyName;
    public string TypeName => typeName;
}